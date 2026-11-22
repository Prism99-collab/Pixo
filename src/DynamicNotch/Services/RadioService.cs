using System.Runtime.InteropServices;
using System.Windows.Threading;
using Windows.Devices.Radios;
using Windows.Networking.Connectivity;

namespace DynamicNotch.Services;

/// <summary>Wi-Fi / Bluetooth radios and airplane mode.</summary>
public sealed class RadioService
{
    readonly Dispatcher _ui;
    Radio? _wifi, _bt;
    IRadioManager? _rm; // airplane mode (lives on an MTA thread)

    public bool HasWifi => _wifi != null;
    public bool HasBluetooth => _bt != null;
    public bool WifiOn => _wifi?.State == RadioState.On;
    public bool BluetoothOn => _bt?.State == RadioState.On;
    public bool AirplaneOn { get; private set; }
    public string? NetworkName { get; private set; }

    public event Action? Changed;

    public RadioService(Dispatcher ui) => _ui = ui;

    public async Task InitAsync()
    {
        try
        {
            await Radio.RequestAccessAsync();
            foreach (var r in await Radio.GetRadiosAsync())
            {
                if (r.Kind == RadioKind.WiFi && _wifi == null) _wifi = r;
                else if (r.Kind == RadioKind.Bluetooth && _bt == null) _bt = r;
            }
            if (_wifi != null) _wifi.StateChanged += (_, _) => _ui.InvokeAsync(() => { Changed?.Invoke(); NetworkMaybeChanged(); });
            if (_bt != null) _bt.StateChanged += (_, _) => _ui.InvokeAsync(() => Changed?.Invoke());
        }
        catch { }

        try { NetworkInformation.NetworkStatusChanged += _ => _ui.InvokeAsync(NetworkMaybeChanged); }
        catch { }
        NetworkName = await Task.Run(ReadNetworkName);

        await Task.Run(() =>
        {
            try
            {
                var t = Type.GetTypeFromCLSID(new Guid("581333F6-28DB-41BE-BC7A-FF201F12F3F6"));
                if (t != null) _rm = (IRadioManager?)Activator.CreateInstance(t);
            }
            catch { _rm = null; }
        });
        await RefreshAirplaneAsync();
        Changed?.Invoke();
    }

    int _netVersion;

    /// <summary>
    /// The network may have changed (these come in bursts of 5–10 while Wi-Fi reconnects or a VPN starts): once things
    /// settle, its name is asked off the screen's thread (Windows' network service can take a second to answer then).
    /// </summary>
    async void NetworkMaybeChanged()
    {
        int v = ++_netVersion;
        await Task.Delay(300);
        if (v != _netVersion) return;
        string? name = await Task.Run(ReadNetworkName);
        if (v != _netVersion) return;
        NetworkName = name;
        Changed?.Invoke();
    }

    static string? ReadNetworkName()
    {
        try
        {
            var p = NetworkInformation.GetInternetConnectionProfile();
            return p != null && p.IsWlanConnectionProfile ? p.ProfileName : null;
        }
        catch { return null; }
    }

    public async Task SetWifiAsync(bool on)
    {
        if (_wifi == null) return;
        try { await _wifi.SetStateAsync(on ? RadioState.On : RadioState.Off); } catch { }
    }

    public async Task SetBluetoothAsync(bool on)
    {
        if (_bt == null) return;
        try { await _bt.SetStateAsync(on ? RadioState.On : RadioState.Off); } catch { }
    }

    public async Task RefreshAirplaneAsync()
    {
        bool? on = await Task.Run<bool?>(() =>
        {
            try
            {
                if (_rm != null && _rm.GetSystemRadioState(out int enabled, out _, out _) == 0) return enabled == 0;
            }
            catch { }
            return null;
        });
        AirplaneOn = on ?? (!WifiOn && !BluetoothOn && (HasWifi || HasBluetooth));
    }

    public async Task SetAirplaneAsync(bool on)
    {
        AirplaneOn = on;
        bool done = await Task.Run(() =>
        {
            try { return _rm != null && _rm.SetSystemRadioState(on ? 0 : 1) == 0; }
            catch { return false; }
        });
        if (!done)
        {
            // fallback: airplane = every radio off
            await SetWifiAsync(!on);
            await SetBluetoothAsync(!on);
        }
        await Task.Delay(300);
        await RefreshAirplaneAsync();
        Changed?.Invoke();
    }

    // Undocumented shell interface behind the airplane-mode switch in Quick Settings.
    [ComImport, Guid("DB3AFBFB-08E6-46C6-AA70-BF9A34C30AB7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IRadioManager
    {
        [PreserveSig] int IsRadioUIEnabled(out int enabled);
        [PreserveSig] int GetSystemRadioState(out int radiosEnabled, out int unk1, out int reason);
        [PreserveSig] int SetSystemRadioState(int radiosEnabled);
        [PreserveSig] int Refresh();
        [PreserveSig] int OnHardwareSliderChange(int a, int b);
    }
}
