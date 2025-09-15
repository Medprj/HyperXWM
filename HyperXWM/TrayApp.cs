namespace HyperXWM;

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using HidSharp;

public sealed class TrayApp : ApplicationContext
{
    // --- Cloud III Wireless ---
    private const int VendorId = 0x03F0; // HP Inc.
    private const int ProductId = 0x05B7; // HyperX Cloud III Wireless dongle
    private const byte BatteryRid = 0x66; // incoming report containing battery level
    private const int BatteryOffset = 4; // incoming report containing battery level
    private const byte QueryRid = 0x66; // byte position with percentage (0..100)
    private const byte BatteryStatusRid = 0x89;
    private const int ConnectionStatusOffset = 1;
    private const byte ConnectionStatusRid = 0x0D;
    private static readonly byte[] QueryPayload = [BatteryStatusRid]; // outgoing report (ping)

    private readonly NotifyIcon _tray;
    private HidDevice? _device;
    private HidStream? _stream;

    private int? _lastBattery;
    private bool _busy; // prevents concurrent update attempts
    private bool _running = true; // main loop control flag

    public TrayApp()
    {
        _tray = new NotifyIcon
        {
            Visible = true,
            Icon = new Icon("icon.ico"),
            Text = "Headset: starting…",
            ContextMenuStrip = BuildMenu()
        };

        _ = Task.Run(() =>
        {
            OpenAsync();
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
        menu.Items.Add(new ToolStripMenuItem("Update now", null, OnUpdateNowClick));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, OnExitClick));

        return menu;
    }

    private void OnUpdateNowClick(object? sender, EventArgs e)
    {
        _ = ManualUpdateAsync();
    }

    private void OnExitClick(object? sender, EventArgs e)
    {
        _running = false;
        _tray.Visible = false;
        Close();
        Application.Exit();
    }

    /// <summary>
    /// Opens a connection to the HID device by VID/PID and sets timeouts.
    /// </summary>
    private void OpenAsync()
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

        while (_running)
        {
            if (_stream is null || _device is null)
            {
                OpenAsync();
                continue;
            }

            try
            {
                var buf = new byte[_device!.GetMaxOutputReportLength()];
                var isSuccess = await TryReadAsync(buf);
                if (isSuccess)
                {
                    if (buf[ConnectionStatusOffset] == ConnectionStatusRid)
                    {
                        UpdateBatteryLevel(0);
                        hasBatteryValue = false;
                        continue;
                    }

                    if (TryParseBattery(buf, out var percent))
                    {
                        UpdateBatteryLevel(percent);
                        hasBatteryValue = true;
                    }
                }
                else
                {
                    if (!hasBatteryValue)
                    {
                        await SendQuery();
                    }
                }
            }
            catch
            {
                Close();
                SetTray("Reconnecting…");
                await Task.Delay(500);
                OpenAsync();
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
                OpenAsync();
            }

            if (_stream is null)
            {
                return;
            }

            await SendQuery();
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// Sends a query command (ping) to the device.
    /// </summary>
    private async Task SendQuery()
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
        outputBuf[0] = QueryRid;
        Array.Copy(QueryPayload, 0, outputBuf, 1, Math.Min(QueryPayload.Length, outputBuf.Length - 1));
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

        if (buf[0] != BatteryRid)
        {
            return false;
        }

        if (buf[1] != QueryPayload[0])
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
    /// Updates the tray icon text when the battery level changes.
    /// </summary>
    private void UpdateBatteryLevel(int percent)
    {
        if (_lastBattery == percent)
        {
            return;
        }

        _lastBattery = percent;

        UpdateTrayIcon(percent);
    }

    /// <summary>
    /// Updates the tray icon text with a custom status message.
    /// </summary>
    private void SetTray(string text)
    {
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
            _tray.Dispose();
            Close();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Redraw tray icon/
    /// </summary>
    /// <param name="percent"></param>
    private void UpdateTrayIcon(int percent)
    {
        percent = Math.Max(0, Math.Min(100, percent));

        var fill = percent switch
        {
            <= 20 => Color.Red,
            <= 30 => Color.Orange,
            <= 50 => Color.LightGreen,
            _ => Color.Chartreuse
        };

        using var bmp = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bmp);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);

        // battery geometry
        // body
        var body = new Rectangle(3, 8, 24, 16); // x,y,w,h
        const int radius = 4; // corner radius
        // terminal ("nose")
        var term = new Rectangle(body.Right + 1, body.Y + 4, 3, body.Height - 8);

        // outline brushes/pens
        using var outlinePen = new Pen(Color.White, 2f);
        using var emptyBrush = new SolidBrush(Color.FromArgb(28, 0, 0, 0)); // subtle empty bg
        using var fillBrush = new SolidBrush(fill);

        using (var bodyPath = CreateRoundedRect(body, radius))
        {
            graphics.FillPath(emptyBrush, bodyPath);
            graphics.DrawPath(outlinePen, bodyPath);
        }

        // draw terminal
        graphics.FillRectangle(emptyBrush, term);
        graphics.DrawRectangle(outlinePen, term);

        // inner fill area (with padding inside the body)
        const int pad = 3;
        var inner = new Rectangle(body.X + pad, body.Y + pad,
            body.Width - pad * 2, body.Height - pad * 2);

        // width proportional to percent
        var fillW = (int)Math.Round(inner.Width * (percent / 100f));
        if (fillW > 0)
        {
            var bar = new Rectangle(inner.X, inner.Y, fillW, inner.Height);
            graphics.FillRectangle(fillBrush, bar);
        }

        // swap tray icon safely (avoid GDI leaks)
        if (_tray.Icon != null)
        {
            _ = DestroyIcon(_tray.Icon.Handle);
            _tray.Icon.Dispose();
        }

        var hIcon = bmp.GetHicon();
        _tray.Icon = Icon.FromHandle(hIcon);

        // tooltip for precise value
        _tray.Text = $"Headset: Battery {percent}%";
        return;

        // rounded rectangle path for body
        GraphicsPath CreateRoundedRect(Rectangle r, int cornerRadius)
        {
            var diameter = cornerRadius * 2;
            var path = new GraphicsPath();
            path.AddArc(r.Left, r.Top, diameter, diameter, 180, 90); // top left
            path.AddArc(r.Right - diameter, r.Top, diameter, diameter, 270, 90); // top right
            path.AddArc(r.Right - diameter, r.Bottom - diameter, diameter, diameter, 0, 90); // bottom right
            path.AddArc(r.Left, r.Bottom - diameter, diameter, diameter, 90, 90); // bottom left
            path.CloseFigure();
            return path;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    static extern bool DestroyIcon(IntPtr handle);
}