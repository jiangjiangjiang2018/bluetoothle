﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Foundation;
using Windows.Devices.Radios;
using Windows.System;
using Plugin.BluetoothLE.Server;


namespace Plugin.BluetoothLE
{
    //BluetoothAdapter.IsPeripheralRoleSupported
    public class Adapter : IAdapter
    {
        readonly BleContext context = new BleContext();
        readonly Subject<bool> scanStatusSubject = new Subject<bool>();
        BluetoothAdapter native;
        Radio radio;


        public Adapter()
        {
        }


        public Adapter(BluetoothAdapter native, Radio radio)
        {
            this.native = native;
            this.radio = radio;
        }


        public string DeviceName => this.radio?.Name;


        public AdapterFeatures Features
        {
            get
            {
                if (!this.native.IsLowEnergySupported)
                    return AdapterFeatures.None;

                var features = AdapterFeatures.AllClient;
                if (!this.native.IsCentralRoleSupported)
                    features &= ~AdapterFeatures.AllClient;

                if (this.native.IsPeripheralRoleSupported)
                    features |= AdapterFeatures.AllServer;

                if (this.native.IsCentralRoleSupported || this.native.IsPeripheralRoleSupported)
                    features |= AdapterFeatures.AllControls;

                return features;
            }
        }


        public IGattServer CreateGattServer() => new GattServer();


        bool isScanning = false;
        public bool IsScanning
        {
            get => this.isScanning;
            private set
            {
                if (this.isScanning == value)
                    return;

                this.isScanning = value;
                this.scanStatusSubject.OnNext(value);
            }
        }


        public AdapterStatus Status
        {
            get
            {
                if (this.radio == null)
                    return AdapterStatus.Unknown;

                switch (this.radio.State)
                {
                    case RadioState.Disabled:
                    case RadioState.Off:
                        return AdapterStatus.PoweredOff;

                    case RadioState.Unknown:
                        return AdapterStatus.Unknown;

                    default:
                        return AdapterStatus.PoweredOn;
                }
            }
        }


        public IDevice GetKnownDevice(Guid deviceId)
        {
            throw new NotImplementedException();
        }



        static readonly IList<IDevice> NullList = new List<IDevice>();
        public IEnumerable<IDevice> GetPairedDevices() => NullList;
        public IEnumerable<IDevice> GetConnectedDevices() => this.context.GetConnectedDevices();


        public IObservable<bool> WhenScanningStatusChanged()
            => this.scanStatusSubject
                .AsObservable()
                .StartWith(this.IsScanning);


        public IObservable<IScanResult> Scan(ScanConfig config)
        {
            if (this.IsScanning)
                throw new ArgumentException("There is already an active scan");

            var observer = Observable.Create<IScanResult>(async ob =>
            {
                await this.EnsureRadio();

                var sub = this.ScanListen().Subscribe(ob.OnNext);
                this.IsScanning = true; // this will actually fire off the scanner

                return () =>
                {
                    this.IsScanning = false;
                    sub.Dispose();
                };
            });
            if (config?.ServiceUuid != null)
            {
                observer = observer.Where(x => x.AdvertisementData?.ServiceUuids.Contains(config.ServiceUuid.Value) ?? false);
            }

            return observer;
        }


        IObservable<IScanResult> scanListenOb;
        public IObservable<IScanResult> ScanListen()
        {
            IDisposable adWatcher = null;
            IDisposable devWatcher = null;

            this.scanListenOb = this.scanListenOb ?? Observable.Create<IScanResult>(ob =>
                this.WhenScanningStatusChanged().Subscribe(scan =>
                {
                    if (!scan)
                    {
                        adWatcher?.Dispose();
                        devWatcher?.Dispose();
                    }
                    else
                    {
                        this.context.Clear();

                        adWatcher = this.context
                            .CreateAdvertisementWatcher()
                            .Subscribe(args =>
                            {
                                var device = this.context.GetDevice(args.BluetoothAddress);
                                if (device == null)
                                {
                                    Debug.WriteLine("Device not found yet - " + args.BluetoothAddress);
                                    // causes Element Not Found exception
                                    //var native = await BluetoothLEDevice.FromBluetoothAddressAsync(args.BluetoothAddress);
                                    //device = this.context.GetDevice(native);
                                    return;
                                }
                                var adData = new AdvertisementData(args);
                                var scanResult = new ScanResult(device, args.RawSignalStrengthInDBm, adData);
                                ob.OnNext(scanResult);
                            });

                        devWatcher = this.context
                            .CreateDeviceWatcher()
                            .Subscribe(async args =>
                            {
                                Log.Write($"[DeviceInfo] Info: {args.Id} / {args.Name}");
                                var native = await BluetoothLEDevice.FromIdAsync(args.Id);

                                Log.Write($"[DeviceInfo] BLE Device: {native.BluetoothAddress} / {native.DeviceId} / {native.Name}");
                                this.context.GetDevice(native); // set discovered device for adscanner to see
                            });
                    }
                })
            )
            .Publish()
            .RefCount();

            return this.scanListenOb;
        }


        IObservable<AdapterStatus> statusOb;
        public IObservable<AdapterStatus> WhenStatusChanged()
        {
            this.statusOb = this.statusOb ?? Observable.Create<AdapterStatus>(async ob =>
            {
                ob.OnNext(this.Status);
                var handler = new TypedEventHandler<Radio, object>((sender, args) =>
                    ob.OnNext(this.Status)
                );
                await this.EnsureRadio();
                this.radio.StateChanged += handler;

                return () => this.radio.StateChanged -= handler;
            })
            .Replay(1)
            .RefCount();

            return this.statusOb;
        }


        IObservable<IDevice> deviceStatusOb;
        public IObservable<IDevice> WhenDeviceStatusChanged()
        {
            this.deviceStatusOb = this.deviceStatusOb ?? Observable.Create<IDevice>(ob =>
            {
                var cleanup = new List<IDisposable>();
                var devices = this.context.GetDiscoveredDevices();

                foreach (var device in devices)
                {
                    cleanup.Add(device
                        .WhenStatusChanged()
                        .Subscribe(_ => ob.OnNext(device))
                    );
                }
                return () => cleanup.ForEach(x => x.Dispose());
            });
            return this.deviceStatusOb;
        }


        public async void OpenSettings()
            => await Launcher.LaunchUriAsync(new Uri("ms-settings:bluetooth"));


        public async void SetAdapterState(bool enable)
        {
            var state = enable ? RadioState.On : RadioState.Off;
            await this.radio.SetStateAsync(state);
        }


        public IObservable<IDevice> WhenDeviceStateRestored() => Observable.Empty<IDevice>();


        async Task EnsureRadio()
        {
            if (this.radio != null)
                return;

            this.native = await BluetoothAdapter.GetDefaultAsync();
            if (this.native == null)
                throw new ArgumentException("No bluetooth adapter found");

            this.radio = await this.native.GetRadioAsync();
        }
    }
}