using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using HidSharp;
using Microsoft.Win32;

namespace HyperXWM;

public sealed class TrayApp : ApplicationContext
{
    // --- Cloud III Wireless ---
    private const int VendorId = 0x03F0; // HP Inc.
    private const int ProductId = 0x05B7; // HyperX Cloud III Wireless dongle

    private const byte ReportId  = 0x66; // constant HID report ID for device status queries and responses
    
    private const byte CablePluggedInStatusRid = 0x8A; // report ID for cable plugged-in status
    private const byte BatteryLevelChangedStatusRid = 0x0C; // report ID for battery level changed event
    private const byte ConnectionStatusRequestRid = 0x82; // report ID for connection status request
    private const byte ConnectionStatusRid = 0x0D; // report ID for connection status response
    private const byte BatteryStatusRid = 0x89; // report ID for battery status query
    private const int BatteryOffset = 4; // incoming report containing battery level

    private const int CommandOffset = 1; // offset of the command field in the report
    private const int StatusOffset = 2; // offset of the status field in the report

    // Boolean-like values returned in device status reports
    private const byte DeviceStatusFalse = 0x00; // represents "false" state in device response
    private const byte DeviceStatusTrue  = 0x01; // represents "true" state in device response

    private readonly NotifyIcon _tray;
    private HidDevice? _device;
    private HidStream? _stream;

    private bool _isCablePluggedIn;
    private bool _isConnected;
    private int? _lastBattery;

    private bool _busy; // prevents concurrent update attempts
    private bool _running = true; // main loop control flag

    /// <summary>
    /// Initializes the tray application.
    /// </summary>
    public TrayApp()
    {
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        
        _tray = new NotifyIcon
        {
            Visible = true,
            Icon = Resources.icon,
            Text = "Headset: starting…",
            ContextMenuStrip = BuildMenu()
        };

        _ = Task.Run(() =>
        {
            Open();
            if (_stream != null)
            {
                _ = RunAsync();
            }
        });
    }

    /// <summary>
    /// Builds the context menu for the tray icon.
    /// </summary>
    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();

        menu.Items.Add(CreateAutostartMenuItem());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Update now", null, OnUpdateNowClick));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, OnExitClick));

        return menu;
    }

    /// <summary>
    /// Creates a context menu item for enabling or disabling application autostart.
    /// </summary>
    private ToolStripMenuItem CreateAutostartMenuItem()
    {
        var autostart = new ToolStripMenuItem("Start with Windows")
        {
            Checked = Autostart.IsEnabled(),
            CheckOnClick = false
        };

        autostart.Click += (_, _) =>
        {
            try
            {
                Autostart.SetEnabled(!autostart.Checked);
                autostart.Checked = Autostart.IsEnabled();
            }
            catch (Exception ex)
            {
                _tray.BalloonTipText = "Autostart error: " + ex.Message;
                _tray.ShowBalloonTip(2000);
                autostart.Checked = Autostart.IsEnabled();
            }
        };

        return autostart;
    }

    /// <summary>
    /// Handles the "Update now" menu click.
    /// Triggers a manual battery status update without blocking the UI thread.
    /// </summary>
    private void OnUpdateNowClick(object? sender, EventArgs e)
    {
        _ = ManualUpdateAsync();
    }

    /// <summary>
    /// Handles the "Exit" menu click.
    /// Stops background tasks, disposes resources, and closes the application.
    /// </summary>
    private void OnExitClick(object? sender, EventArgs e)
    {
        _running = false;
        _tray.Visible = false;
        Close();
        Application.Exit();
    }
    
    /// <summary>
    /// Handles system power mode changes (resume/suspend) to open or close the device connection.
    /// </summary>
    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            Open();
        }
        else if (e.Mode == PowerModes.Suspend)
        {
            Close();
        }
    }

    /// <summary>
    /// Opens a connection to the HID device by VID/PID and sets timeouts.
    /// </summary>
    private void Open()
    {
        Close();
        var device = DeviceList.Local.GetHidDevices()
            .FirstOrDefault(d =>
                d.VendorID == VendorId &&
                d.ProductID == ProductId &&
                d.GetMaxOutputReportLength() > 0);

        if (device == null)
        {
            SetTray("Device not found");
            return;
        }

        if (!device.TryOpen(out var stream))
        {
            SetTray("Can't open device");
            return;
        }

        stream.ReadTimeout = 1500;
        stream.WriteTimeout = 1000;

        _device = device;
        _stream = stream;
        SetTray("Connected");
    }

    /// <summary>
    /// Main loop: listens for incoming reports and requests battery state if necessary.
    /// </summary>
    private async Task RunAsync()
    {
        if (_stream is null || _device is null)
        {
            return;
        }

        var hasBatteryValue = false;
        
        // init commands
        await SendQuery([ConnectionStatusRequestRid]); 
        await SendQuery([CablePluggedInStatusRid]);
        await SendQuery([BatteryStatusRid]);

        while (_running)
        {
            if (_stream is null || _device is null)
            {
                Open();
                continue;
            }

            try
            {
                var buf = new byte[_device!.GetMaxOutputReportLength()];
                var isSuccess = await TryReadAsync(buf);
                if (isSuccess)
                {
                    switch (buf[CommandOffset])
                    {
                        case ConnectionStatusRid:
                        case ConnectionStatusRequestRid:
                            _isConnected = buf[StatusOffset] != DeviceStatusFalse;
                            if (_isConnected)
                            {
                                await SendQuery([CablePluggedInStatusRid]);
                                await SendQuery([BatteryStatusRid]); 
                            }
                            else
                            {
                                UpdateTrayIcon(0);
                            }

                            hasBatteryValue = false;
                            continue;

                        case CablePluggedInStatusRid:
                            _isCablePluggedIn = buf[StatusOffset] == DeviceStatusTrue;
                            UpdateTrayIcon(_lastBattery ?? 0);
                            continue;

                        case BatteryLevelChangedStatusRid:
                            _isCablePluggedIn = buf[StatusOffset] == DeviceStatusTrue;
                            hasBatteryValue = false;
                            continue;

                        case BatteryStatusRid when TryParseBattery(buf, out var percent):
                            UpdateTrayIcon(percent);
                            hasBatteryValue = true;
                            break;
                    }
                }
                else
                {
                    if (_isConnected)
                    {
                        if (!hasBatteryValue)
                        {
                            await SendQuery([BatteryStatusRid]);
                        }
                    }
                }
            }
            catch
            {
                Close();
                SetTray("Reconnecting…");
                await Task.Delay(500);
                Open();
            }
        }
    }

    /// <summary>
    /// Performs a manual update request from the tray menu.
    /// </summary>
    private async Task ManualUpdateAsync()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        try
        {
            if (_stream is null || _device is null)
            {
                Open();
            }

            if (_stream is null)
            {
                return;
            }

            await SendQuery([BatteryStatusRid]);
        }
        finally
        {
            _busy = false;
        }
    }


    /// <summary>
    /// Sends a query command (ping) to the device.
    /// </summary>
    private async Task SendQuery(byte[] queryPayload)
    {
        if (_stream is null || _device is null)
        {
            return;
        }

        var outputLength = _device.GetMaxOutputReportLength();
        if (outputLength <= 0)
        {
            return;
        }

        var outputBuf = new byte[outputLength];
        outputBuf[0] = ReportId;
        Array.Copy(queryPayload, 0, outputBuf, 1, Math.Min(queryPayload.Length, outputBuf.Length - 1));
        await _stream.WriteAsync(outputBuf);
    }

    /// <summary>
    /// Attempts to read a single input report asynchronously.
    /// Returns true if any data was read.
    /// </summary>
    private async Task<bool> TryReadAsync(Memory<byte> buf)
    {
        try
        {
            return await _stream!.ReadAsync(buf) > 0;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// Attempts to parse a battery percentage from a raw report buffer.
    /// </summary>
    private static bool TryParseBattery(byte[] buf, out int percent)
    {
        percent = -1;
        if (buf.Length <= BatteryOffset)
        {
            return false;
        }

        if (buf[0] != ReportId)
        {
            return false;
        }

        if (buf[1] != BatteryStatusRid)
        {
            return false;
        }

        int value = buf[BatteryOffset];
        if (value is >= 0 and <= 100)
        {
            percent = value;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Updates the tray icon text with a custom status message.
    /// </summary>
    private void SetTray(string text)
    {
        if (_tray.Icon != null)
        {
            _ = DestroyIcon(_tray.Icon.Handle);
            _tray.Icon.Dispose();
        }

        _tray.Icon = Resources.Dis;
        _tray.Text = $"Headset: {text}";
    }

    /// <summary>
    /// Closes the device stream and releases resources.
    /// </summary>
    private void Close()
    {
        try
        {
            _stream?.Dispose();
        }
        catch
        {
            // ignore errors on dispose
        }

        _stream = null;
        _device = null;
    }

    /// <summary>
    /// Releases resources.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            _tray.Dispose();
            Close();
        }

        base.Dispose(disposing);
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool DestroyIcon(IntPtr handle);

    /// <summary>
    /// Updates the tray icon text when the battery level changes.
    /// </summary>
    private void UpdateTrayIcon(int percent)
    {
        if (_tray.Icon != null)
        {
            _ = DestroyIcon(_tray.Icon.Handle);
            _tray.Icon.Dispose();
        }

        if (!_isConnected)
        {
            SetDisconnectedIcon();
            return;
        }

        if (_isCablePluggedIn)
        {
            SetChargingIcon();

            return;
        }

        SetBatteryLevelIcon(percent);
        _lastBattery = percent;
    }

    /// <summary>
    /// Updates the tray icon and tooltip text based on the current battery percentage.
    /// </summary>
    private void SetBatteryLevelIcon(int percent)
    {
        _tray.Icon = percent switch
        {
            0 => Resources.Empty,
            <= 20 => Resources.b20,
            <= 50 => Resources.b50,
            <= 70 => Resources.b70,
            > 70 => Resources.b100
        };

        _tray.Text = $"Headset: Battery {percent}%";
    }

    /// <summary>
    /// Sets the tray icon and tooltip to indicate that the headset is charging.
    /// </summary>
    private void SetChargingIcon()
    {
        _tray.Icon = Resources.Ch;
        _tray.Text = "Headset: Charging…";
    }

    /// <summary>
    /// Sets the tray icon and tooltip to indicate that the headset is disconnected.
    /// </summary>
    private void SetDisconnectedIcon()
    {
        _tray.Icon = Resources.Dis;
        _tray.Text = "Headset: Charging…";
    }
}