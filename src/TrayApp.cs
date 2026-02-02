using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using HidSharp;
using Microsoft.Win32;
using Serilog;

namespace HyperXWM;

public sealed class TrayApp : ApplicationContext
{
    // --- Cloud III Wireless ---
    private const string DeviceName = "HyperX Cloud III";

    private const int VendorId = 0x03F0; // HP Inc.
    private const int ProductId = 0x05B7; // HyperX Cloud III Wireless dongle

    private const byte ReportId = 0x66; // constant HID report ID for device status queries and responses

    private const byte CablePluggedInStatusRid = 0x8A; // report ID for cable plugged-in status
    private const byte BatteryLevelChangedStatusRid = 0x0C; // report ID for battery level changed event
    private const byte ConnectionStatusRequestRid = 0x82; // report ID for connection status request
    private const byte ConnectionStatusRid = 0x0D; // report ID for connection status response
    private const byte BatteryStatusRid = 0x89; // report ID for battery status query
    private const int BatteryOffset = 4; // incoming report containing battery level

    private const int ReportIdOffset = 0; // offset of the report ID field in the report
    private const int CommandOffset = 1; // offset of the command field in the report
    private const int StatusOffset = 2; // offset of the status field in the report

    // Boolean-like values returned in device status reports
    private const byte DeviceStatusFalse = 0x00; // represents "false" state in device response
    private const byte DeviceStatusTrue = 0x01; // represents "true" state in device response

    private HidDevice? _device;
    private HidStream? _stream;

    private bool _isCablePluggedIn;
    private bool _isConnected;

    private readonly SynchronizationContext _ui;
    private readonly NotifyIcon _tray;
    private Icon? _currentIcon;

    private bool _busy; // prevents concurrent update attempts
    private string? _lastError;

    private CancellationTokenSource _cts = new();
    private Task? _workerLoopTask;
    private readonly object _sync = new();

    private bool _disposed;

    /// <summary>
    /// Initializes the tray application.
    /// </summary>
    public TrayApp()
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        _tray = new NotifyIcon
        {
            Visible = true,
            Icon = Resources.icon,
            Text = "Headset: starting…",
            ContextMenuStrip = BuildMenu()
        };

        Log.Information("Application started...");

        StartWorker();
    }

    private void Ui(Action action)
    {
        _ui.Post(_ => action(), null);
    }

    /// <summary>
    /// Handles system power mode changes (resume/suspend) to open or close the device connection.
    /// </summary>
    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        switch (e.Mode)
        {
            case PowerModes.Suspend:
                StopWorker();
                Log.Information("System suspended");
                break;

            case PowerModes.Resume:
                StartWorker();
                Log.Information("System resumed");
                break;

            case PowerModes.StatusChange:
            default:
                break;
        }
    }

    /// <summary>
    /// Builds the context menu for the tray icon.
    /// </summary>
    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();

        menu.Items.Add(CreateAutostartMenuItem());
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
                ShowBalloonTip("Error", "Autostart error: " + ex.Message, ToolTipIcon.Error);
                autostart.Checked = Autostart.IsEnabled();
            }
        };

        return autostart;
    }

    /// <summary>
    /// Handles the "Update now" menu click.
    /// Triggers a manual battery status update without blocking the UI thread.
    /// </summary>
    private void OnReconnectClick(object? sender, EventArgs e)
    {
        _ = ReconnectAsync();
    }

    /// <summary>
    /// Handles the "Exit" menu click.
    /// Stops background tasks, disposes resources, and closes the application.
    /// </summary>
    private void OnExitClick(object? sender, EventArgs e)
    {
        Dispose();
        Application.Exit();
    }

    /// <summary>
    /// Starts the background worker loop if it is not already running.
    /// </summary>
    private void StartWorker()
    {
        lock (_sync)
        {
            if (_workerLoopTask is { IsCompleted: false })
            {
                return;
            }

            if (_cts.IsCancellationRequested)
            {
                _cts.Dispose();
                _cts = new CancellationTokenSource();
            }

            _workerLoopTask = RunWorkerLoopAsync(_cts.Token);
        }
    }

    /// <summary>
    /// Stops the background worker loop by requesting cancellation
    /// and closing the current device connection.
    /// </summary>
    private void StopWorker()
    {
        lock (_sync)
        {
            if (_cts.IsCancellationRequested)
            {
                return;
            }

            _cts.Cancel();
            Close();
        }
    }

    /// <summary>
    /// Runs the background worker loop with automatic restart on failures.
    /// </summary>
    private async Task RunWorkerLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunWorkerOnceAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Log.Debug($"ct.IsCancellationRequested");
                return;
            }
            catch (Exception ex)
            {
                Log.Error($"Exception in method 'RunWorkerLoopAsync' {ex}");
                
                Close();

                SetDisconnectedIcon();

                if (_lastError != ex.Message)
                {
                    _lastError = ex.Message;
                    ShowBalloonTip("Error", ex.Message, ToolTipIcon.Error);
                }
                
                try 
                { 
                    await Task.Delay(TimeSpan.FromSeconds(2), ct); 
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Executes a single worker session:
    /// opens the device connection, sends initial queries,
    /// and starts listening for incoming reports.
    /// </summary>
    private async Task RunWorkerOnceAsync(CancellationToken ct)
    {
        Open();

        if (_stream is null || _device is null)
        {
            throw new InvalidOperationException("Failed to open device stream");
        }

        await Ping(ct);
        await ListenDeviceAsync(ct);
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
                d.GetMaxOutputReportLength() == 62);

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
    /// Sends status queries to check connection, cable, and battery states.
    /// </summary>
    private async Task Ping(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        await SendQuery([ConnectionStatusRequestRid], ct);
        await SendQuery([CablePluggedInStatusRid], ct);
        await SendQuery([BatteryStatusRid], ct);
    }

    /// <summary>
    /// Listens for incoming reports and requests battery state if necessary.
    /// </summary>
    private async Task ListenDeviceAsync(CancellationToken ct)
    {
        if (_stream is null || _device is null)
        {
            throw new InvalidOperationException("Device is not open");
        }

        var hasBatteryValue = false;
        var buf = new byte[_device.GetMaxOutputReportLength()];

        while (!ct.IsCancellationRequested)
        {
            ct.ThrowIfCancellationRequested();

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
                            await SendQuery([CablePluggedInStatusRid], ct);
                            await SendQuery([BatteryStatusRid], ct);
                        }
                        else
                        {
                            UpdateBatteryStatus(0);
                        }

                        hasBatteryValue = false;
                        continue;

                    case CablePluggedInStatusRid:
                    case BatteryLevelChangedStatusRid:
                        _isCablePluggedIn = buf[StatusOffset] == DeviceStatusTrue;
                        hasBatteryValue = false;
                        continue;

                    case BatteryStatusRid when TryParseBattery(buf, out var percent):
                        UpdateBatteryStatus(percent);
                        hasBatteryValue = true;
                        break;
                }
            }
            else
            {
                if (_isConnected && !hasBatteryValue)
                {
                    await SendQuery([BatteryStatusRid], ct);
                }
            }
        }
    }

    /// <summary>
    /// Forces a full worker restart to recover from connection or device errors.
    /// </summary>
    private async Task ReconnectAsync()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        try
        {
            SetTray("Restarting…");

            Task? oldTask;
            lock (_sync)
            {
                oldTask = _workerLoopTask;
            }

            StopWorker();

            if (oldTask is not null)
            {
                await Task.WhenAny(oldTask, Task.Delay(1500));
            }

            StartWorker();
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// Sends a query command to the device.
    /// </summary>
    private async Task SendQuery(byte[] queryPayload, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

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
        outputBuf[ReportIdOffset] = ReportId;
        Array.Copy(queryPayload, 0, outputBuf, 1, Math.Min(queryPayload.Length, outputBuf.Length - 1));
        await _stream.WriteAsync(outputBuf, ct);
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

        if (buf[ReportIdOffset] != ReportId)
        {
            return false;
        }

        if (buf[CommandOffset] != BatteryStatusRid)
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
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (!disposing)
        {
            return;
        }

        SystemEvents.PowerModeChanged -= OnPowerModeChanged;

        StopWorker();

        lock (_sync)
        {
            _cts.Dispose();
        }

        try
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        catch
        {
            // ignore cleanup errors on shutdown.
        }
    }

    /// <summary>
    /// Updates the battery status.
    /// </summary>
    private void UpdateBatteryStatus(int percent)
    {
        if (!_isConnected)
        {
            SetDisconnectedIcon();
            return;
        }

        if (_isCablePluggedIn)
        {
            SetChargingIcon(percent);
            return;
        }

        SetBatteryLevelIcon(percent);
    }

    /// <summary>
    /// Updates the tray icon and tooltip text based on the current battery percentage.
    /// </summary>
    private void SetBatteryLevelIcon(int percent)
    {
        var icon = percent switch
        {
            0 => Resources.empty,
            <= 10 => Resources.b10,
            <= 20 => Resources.b20,
            <= 50 => Resources.b50,
            <= 60 => Resources.b60,
            > 60 => Resources.b100
        };

        SetTray($"Battery {percent}%", icon);
    }

    /// <summary>
    /// Sets the tray icon and tooltip to indicate that the headset is charging.
    /// </summary>
    private void SetChargingIcon(int percent)
    {
        var icon = percent switch
        {
            0 => Resources.empty_ch,
            <= 10 => Resources.b10_ch,
            <= 20 => Resources.b20_ch,
            <= 50 => Resources.b50_ch,
            <= 60 => Resources.b60_ch,
            > 60 => Resources.b100_ch
        };

        SetTray($"Charging… {percent}%", icon);
    }

    /// <summary>
    /// Sets the tray icon and tooltip to indicate that the headset is disconnected.
    /// </summary>
    private void SetDisconnectedIcon()
    {
        SetTray("Disconnected", Resources.dis);
    }

    /// <summary>
    /// Updates the tray icon text with a custom status message.
    /// </summary>
    private void SetTray(string text, Icon? icon = null)
    {
        _ui.Post(_ =>
        {
            _tray.Text = $"{DeviceName}: {Truncate(text, 50)}";

            if (icon is null)
            {
                return;
            }

            _currentIcon?.Dispose();
            _currentIcon = (Icon)icon.Clone();
            _tray.Icon = _currentIcon;
        }, null);
    }

    /// <summary>
    /// Displays a tray balloon notification.
    /// </summary>
    private void ShowBalloonTip(
        string title,
        string message,
        ToolTipIcon icon = ToolTipIcon.Info,
        int timeoutMs = 3000)
    {
        _ui.Post(_ =>
        {
            try
            {
                _tray.ShowBalloonTip(timeoutMs, title, message, icon);
            }
            catch
            {
                // Ignore UI errors during shutdown or disposal.
            }
        }, null);
    }

    /// <summary>
    /// Truncates the specified text to the given maximum length,
    /// appending an ellipsis if truncation is required.
    /// </summary>
    private static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
        {
            return text;
        }

        return text[..(maxLength - 1)] + "…";
    }
}