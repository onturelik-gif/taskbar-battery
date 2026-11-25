using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace TaskbarBattery
{
    public struct DeviceBattery
    {
        public bool Connected;
        public int Percent;
        public bool Charging;
        public bool ChargingFull;
    }

    static class HidReader
    {
        static Guid GUID_DEVINTERFACE_HID = new Guid("4D1E55B2-F16F-11CF-88CB-001111000030");

        [StructLayout(LayoutKind.Sequential)]
        struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct HIDD_ATTRIBUTES
        {
            public int Size;
            public ushort VendorID;
            public ushort ProductID;
            public ushort VersionNumber;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct HIDP_CAPS
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
            public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps;
            public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps;
            public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps;
            public ushort NumberFeatureDataIndices;
        }

        [DllImport("setupapi.dll", SetLastError = true)]
        static extern IntPtr SetupDiGetClassDevs(ref Guid ClassGuid, IntPtr Enumerator, IntPtr hwndParent, uint Flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiEnumDeviceInterfaces(IntPtr DeviceInfoSet, IntPtr DeviceInfoData, ref Guid InterfaceClassGuid, uint MemberIndex, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData);

        [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr DeviceInfoSet, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData, IntPtr DeviceInterfaceDetailData, uint DeviceInterfaceDetailDataSize, out uint RequiredSize, IntPtr DeviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("hid.dll", SetLastError = true)]
        static extern bool HidD_GetAttributes(SafeFileHandle HidDeviceObject, ref HIDD_ATTRIBUTES Attributes);

        [DllImport("hid.dll", SetLastError = true)]
        static extern bool HidD_GetPreparsedData(SafeFileHandle HidDeviceObject, out IntPtr PreparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        static extern bool HidD_FreePreparsedData(IntPtr PreparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        static extern int HidP_GetCaps(IntPtr PreparsedData, out HIDP_CAPS Capabilities);

        [DllImport("hid.dll", SetLastError = true)]
        static extern bool HidD_SetFeature(SafeFileHandle HidDeviceObject, byte[] lpReportBuffer, int ReportBufferLength);

        [DllImport("hid.dll", SetLastError = true)]
        static extern bool HidD_GetFeature(SafeFileHandle HidDeviceObject, byte[] lpReportBuffer, int ReportBufferLength);

        [DllImport("hid.dll", SetLastError = true)]
        static extern bool HidD_FlushQueue(SafeFileHandle HidDeviceObject);

        public static void ReadAll(out DeviceBattery mouse, out DeviceBattery headset)
        {
            mouse = new DeviceBattery { Connected = false, Percent = -1, Charging = false };
            headset = new DeviceBattery { Connected = false, Percent = -1, Charging = false };

            IntPtr devInfo = SetupDiGetClassDevs(ref GUID_DEVINTERFACE_HID, IntPtr.Zero, IntPtr.Zero, 0x02 | 0x10);
            if (devInfo == IntPtr.Zero || devInfo.ToInt64() == -1) return;

            try
            {
                var data = new SP_DEVICE_INTERFACE_DATA();
                data.cbSize = Marshal.SizeOf(data);
                uint idx = 0;

                while (SetupDiEnumDeviceInterfaces(devInfo, IntPtr.Zero, ref GUID_DEVINTERFACE_HID, idx++, ref data))
                {
                    uint reqSize = 0;
                    SetupDiGetDeviceInterfaceDetail(devInfo, ref data, IntPtr.Zero, 0, out reqSize, IntPtr.Zero);
                    if (reqSize == 0) continue;

                    IntPtr detail = Marshal.AllocHGlobal((int)reqSize);
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetail(devInfo, ref data, detail, reqSize, out reqSize, IntPtr.Zero))
                            continue;

                        string path = Marshal.PtrToStringAuto(IntPtr.Add(detail, 4));
                        if (string.IsNullOrEmpty(path)) continue;
                        string lp = path.ToLowerInvariant();

                        bool isRazer = lp.Contains("vid_1532") && (lp.Contains("pid_00b7") || lp.Contains("pid_00b6"));
                        bool isCorsair = lp.Contains("vid_1b1c") && lp.Contains("pid_0a51");
                        if (!isRazer && !isCorsair) continue;

                        if (isRazer && !mouse.Connected)
                        {
                            TryReadRazer(path, ref mouse);
                        }
                        else if (isCorsair && !headset.Connected)
                        {
                            TryReadCorsair(path, ref headset);
                        }
                    }
                    catch { }
                    finally
                    {
                        Marshal.FreeHGlobal(detail);
                    }
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(devInfo);
            }
        }

        static bool SendRazerCommand(SafeFileHandle h, byte tx, byte cmdId, out byte value)
        {
            value = 0;
            byte[] buf = new byte[91];
            buf[0] = 0x00; // report id
            buf[2] = tx;   // transaction_id
            buf[6] = 0x02; // data size
            buf[7] = 0x07; // command class: power
            buf[8] = cmdId;
            byte crc = 0;
            for (int i = 3; i <= 88; i++) crc ^= buf[i];
            buf[89] = crc;

            if (!HidD_SetFeature(h, buf, buf.Length)) return false;
            Thread.Sleep(55);

            byte[] resp = new byte[91];
            resp[0] = 0x00;
            if (!HidD_GetFeature(h, resp, resp.Length)) return false;

            if (resp[1] == 0x02 && resp[7] == 0x07 && resp[8] == cmdId)
            {
                value = resp[10];
                return true;
            }
            return false;
        }

        static void TryReadRazer(string path, ref DeviceBattery mouse)
        {
            using (var h = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
            {
                if (h.IsInvalid) return;
                IntPtr pp;
                if (!HidD_GetPreparsedData(h, out pp)) return;
                HIDP_CAPS caps;
                HidP_GetCaps(pp, out caps);
                HidD_FreePreparsedData(pp);

                if (caps.FeatureReportByteLength != 91) return;

                byte rawBat;
                if (SendRazerCommand(h, 0x1F, 0x80, out rawBat) || SendRazerCommand(h, 0x3F, 0x80, out rawBat))
                {
                    int pct = (int)Math.Round(rawBat * 100.0 / 255.0);
                    if (pct > 100) pct = 100;
                    if (pct < 0) pct = 0;

                    byte rawChg = 0;
                    bool chg = false;
                    if (SendRazerCommand(h, 0x1F, 0x84, out rawChg))
                    {
                        chg = rawChg != 0;
                    }

                    // If rawBat == 0 and not charging, mouse may be asleep/off
                    if (rawBat > 0 || chg)
                    {
                        mouse.Connected = true;
                        mouse.Percent = pct;
                        mouse.Charging = chg;
                    }
                }
            }
        }

        static void TryReadCorsair(string path, ref DeviceBattery headset)
        {
            // First check UsagePage == 0xFFC5 (In=5, Out=20)
            using (var h0 = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
            {
                if (h0.IsInvalid) return;
                IntPtr pp;
                if (!HidD_GetPreparsedData(h0, out pp)) return;
                HIDP_CAPS caps;
                HidP_GetCaps(pp, out caps);
                HidD_FreePreparsedData(pp);

                if (caps.UsagePage != 0xFFC5 || caps.InputReportByteLength < 5 || caps.OutputReportByteLength < 2)
                    return;
            }

            using (var hrw = CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero))
            {
                if (hrw.IsInvalid) return;
                HidD_FlushQueue(hrw);
                using (var fs = new FileStream(hrw, FileAccess.ReadWrite, 5, true))
                {
                    byte[] req = new byte[20];
                    req[0] = 0xC9;
                    req[1] = 0x64;
                    fs.Write(req, 0, req.Length);

                    byte[] rep = new byte[5];
                    for (int tries = 0; tries < 5; tries++)
                    {
                        var ar = fs.BeginRead(rep, 0, rep.Length, null, null);
                        if (!ar.AsyncWaitHandle.WaitOne(350)) break;
                        int n = fs.EndRead(ar);
                        if (n >= 5 && rep[0] == 0x64)
                        {
                            // rep[4] == 0 means headset is off/disconnected
                            if (rep[4] == 0)
                            {
                                headset.Connected = false;
                                headset.Percent = -1;
                                headset.Charging = false;
                            }
                            else
                            {
                                int pct = rep[2] & 0x7F;
                                if (pct > 100) pct = 100;
                                // On Corsair Void Elite: bit 7 of rep[2] (0x80) is MIC-UP, NOT charging!
                                // rep[4]: 1=normal, 2=low, 5=actively charging (firmware reports fake 100%), 4=charging complete
                                bool chg = (rep[4] == 4 || rep[4] == 5);
                                bool chgFull = (rep[4] == 4);
                                headset.Connected = true;
                                headset.Percent = pct;
                                headset.Charging = chg;
                                headset.ChargingFull = chgFull;
                            }
                            break;
                        }
                    }
                }
            }
        }
    }

    public class BatteryWidgetForm : Form
    {
        const int WS_EX_LAYERED = 0x80000;
        const int WS_EX_TOOLWINDOW = 0x00000080;
        const int WS_EX_TOPMOST = 0x00000008;
        const int WS_EX_NOACTIVATE = 0x08000000;

        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        const uint SWP_NOSIZE = 0x0001;
        const uint SWP_NOMOVE = 0x0002;
        const uint SWP_NOACTIVATE = 0x0010;
        const uint SWP_SHOWWINDOW = 0x0040;

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        struct SIZE { public int cx, cy; }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [DllImport("user32.dll")] static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll")] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")] static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
        [DllImport("user32.dll", EntryPoint = "SetWindowLong")] static extern IntPtr SetWindowLong32(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
        static IntPtr SetWindowOwner(IntPtr hWnd, IntPtr owner)
        {
            return IntPtr.Size == 8 ? SetWindowLongPtr64(hWnd, -8, owner) : SetWindowLong32(hWnd, -8, owner);
        }
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hDC);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hDC);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr hObj);
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

        [DllImport("shell32.dll")]
        static extern int SHQueryUserNotificationState(out int pquns);

        DeviceBattery _mouse = new DeviceBattery { Connected = false, Percent = -1 };
        DeviceBattery _headset = new DeviceBattery { Connected = false, Percent = -1 };
        int _lastKnownHeadsetPct = -1;
        int _offsetX = 0;
        IntPtr _attachedTaskbar = IntPtr.Zero;
        bool _isPolling = false;
        readonly string _configPath;
        readonly System.Windows.Forms.Timer _pollTimer;
        readonly System.Windows.Forms.Timer _posTimer;
        readonly ContextMenuStrip _menu;
        readonly ToolStripMenuItem _startupItem;
        readonly ToolStripMenuItem _infoItem;

        const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string AppRegName = "TaskbarBatteryWidget";

        public BatteryWidgetForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(76, 48);

            string appDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaskbarBattery");
            Directory.CreateDirectory(appDir);
            _configPath = Path.Combine(appDir, "offset.txt");
            LoadOffset();

            _menu = new ContextMenuStrip();
            _infoItem = new ToolStripMenuItem("Yükleniyor...") { Enabled = false };
            var refreshItem = new ToolStripMenuItem("Şimdi Yenile", null, (s, e) => PollDevicesAsync());
            var moveLeftItem = new ToolStripMenuItem("Sola Kaydır (-6px)", null, (s, e) => { _offsetX -= 6; SaveOffset(); UpdatePosition(); });
            var moveRightItem = new ToolStripMenuItem("Sağa Kaydır (+6px)", null, (s, e) => { _offsetX += 6; SaveOffset(); UpdatePosition(); });
            var resetPosItem = new ToolStripMenuItem("Konumu Sıfırla", null, (s, e) => { _offsetX = 0; SaveOffset(); UpdatePosition(); });
            _startupItem = new ToolStripMenuItem("Windows ile Başlat", null, ToggleStartup) { Checked = IsStartupEnabled() };
            var exitItem = new ToolStripMenuItem("Çıkış", null, (s, e) => Close());

            _menu.Items.AddRange(new ToolStripItem[]
            {
                _infoItem,
                new ToolStripSeparator(),
                refreshItem,
                moveLeftItem,
                moveRightItem,
                resetPosItem,
                new ToolStripSeparator(),
                _startupItem,
                new ToolStripSeparator(),
                exitItem
            });

            MouseUp += (s, e) =>
            {
                if (e.Button == MouseButtons.Right)
                {
                    _menu.Show(Cursor.Position);
                }
                else if (e.Button == MouseButtons.Left)
                {
                    PollDevicesAsync();
                }
            };

            _pollTimer = new System.Windows.Forms.Timer { Interval = 3000 };
            _pollTimer.Tick += (s, e) => PollDevicesAsync();

            _posTimer = new System.Windows.Forms.Timer { Interval = 1200 };
            _posTimer.Tick += (s, e) => UpdatePosition();

            Load += (s, e) =>
            {
                UpdatePosition();
                RenderWidget();
                PollDevicesAsync();
                _pollTimer.Start();
                _posTimer.Start();
            };
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        void LoadOffset()
        {
            try
            {
                if (File.Exists(_configPath))
                {
                    int val;
                    if (int.TryParse(File.ReadAllText(_configPath).Trim(), out val))
                        _offsetX = val;
                }
            }
            catch { }
        }

        void SaveOffset()
        {
            try { File.WriteAllText(_configPath, _offsetX.ToString()); } catch { }
        }

        bool IsStartupEnabled()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    return key != null && key.GetValue(AppRegName) != null;
                }
            }
            catch { return false; }
        }

        void ToggleStartup(object sender, EventArgs e)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (key == null) return;
                    if (IsStartupEnabled())
                    {
                        key.DeleteValue(AppRegName, false);
                        _startupItem.Checked = false;
                    }
                    else
                    {
                        key.SetValue(AppRegName, "\"" + Application.ExecutablePath + "\"");
                        _startupItem.Checked = true;
                    }
                }
            }
            catch { }
        }

        bool IsFullscreenAppActive()
        {
            try
            {
                int state;
                if (SHQueryUserNotificationState(out state) == 0)
                {
                    // 2 = QUNS_BUSY (fullscreen app), 3 = QUNS_RUNNING_D3D_FULL_SCREEN
                    if (state == 2 || state == 3) return true;
                }
            }
            catch { }
            return false;
        }

        void UpdatePosition()
        {
            if (IsFullscreenAppActive())
            {
                if (Visible) Hide();
                return;
            }
            if (!Visible) Show();

            int targetW = 76;
            int targetH = 48;
            int targetX;
            int targetY;

            IntPtr tb = FindWindow("Shell_TrayWnd", null);
            RECT tbRect;
            if (tb != IntPtr.Zero && GetWindowRect(tb, out tbRect) && (tbRect.Bottom - tbRect.Top) > 10)
            {
                // Attach Shell_TrayWnd as our window owner so DWM atomically keeps us above the taskbar
                // even when clicking the hidden icons chevron (^) or tray flyouts — zero flicker/disappear!
                if (_attachedTaskbar != tb && IsHandleCreated)
                {
                    SetWindowOwner(Handle, tb);
                    _attachedTaskbar = tb;
                }

                targetH = tbRect.Bottom - tbRect.Top;
                targetY = tbRect.Top;

                IntPtr tn = FindWindowEx(tb, IntPtr.Zero, "TrayNotifyWnd", null);
                RECT tnRect;
                if (tn != IntPtr.Zero && GetWindowRect(tn, out tnRect) && (tnRect.Right - tnRect.Left) > 20)
                {
                    targetX = tnRect.Left - targetW + 26 + _offsetX;
                }
                else
                {
                    targetX = tbRect.Right - 216 - targetW + _offsetX;
                }
            }
            else
            {
                Rectangle scr = Screen.PrimaryScreen.Bounds;
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                targetH = Math.Max(40, scr.Bottom - wa.Bottom);
                targetY = scr.Bottom - targetH;
                targetX = scr.Right - 245 - targetW + _offsetX;
            }

            bool posChanged = (Left != targetX || Top != targetY);
            bool sizeChanged = (Width != targetW || Height != targetH);
            if (posChanged || sizeChanged)
            {
                Location = new Point(targetX, targetY);
                if (sizeChanged) Size = new Size(targetW, targetH);
                RenderWidget();
                SetWindowPos(Handle, HWND_TOPMOST, targetX, targetY, targetW, targetH, SWP_NOACTIVATE | SWP_SHOWWINDOW);
            }
            else if (_attachedTaskbar == IntPtr.Zero)
            {
                SetWindowPos(Handle, HWND_TOPMOST, targetX, targetY, targetW, targetH, SWP_NOACTIVATE | SWP_SHOWWINDOW);
            }
        }

        void PollDevicesAsync()
        {
            if (_isPolling) return;
            _isPolling = true;

            Task.Run(() =>
            {
                DeviceBattery m, h;
                HidReader.ReadAll(out m, out h);
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        _mouse = m;
                        if (h.Connected && !h.Charging && h.Percent >= 0)
                        {
                            _lastKnownHeadsetPct = h.Percent;
                        }
                        else if (h.Connected && h.Charging && !h.ChargingFull && h.Percent >= 99)
                        {
                            // Corsair Void Elite firmware reports 100% while USB charging due to voltage jump
                            h.Percent = _lastKnownHeadsetPct; // -1 if unknown, otherwise last real level
                        }
                        _headset = h;
                        string mText = m.Connected ? (m.Percent + "%" + (m.Charging ? " (Şarjda)" : "")) : "Kapalı";
                        string hText;
                        if (!h.Connected) hText = "Kapalı";
                        else if (h.Charging && h.Percent < 0) hText = "Şarj Oluyor";
                        else hText = h.Percent + "%" + (h.Charging ? (h.ChargingFull ? " (Dolu)" : " (Şarjda)") : "");
                        _infoItem.Text = "Mouse: " + mText + "  |  Kulaklık: " + hText;
                        RenderWidget();
                        _isPolling = false;
                    }));
                }
                catch
                {
                    _isPolling = false;
                }
            });
        }

        static Color GetStatusColor(DeviceBattery b)
        {
            if (!b.Connected) return Color.FromArgb(135, 255, 255, 255);
            if (b.Charging) return Color.FromArgb(245, 110, 231, 183); // soft mint green
            if (b.Percent <= 15) return Color.FromArgb(250, 248, 113, 113); // soft red
            if (b.Percent <= 25) return Color.FromArgb(250, 251, 191, 36);  // soft amber
            return Color.FromArgb(248, 255, 255, 255); // crisp white
        }

        static void DrawMouseIcon(Graphics g, float x, float y, float size, Color color)
        {
            using (var pen = new Pen(color, 1.35f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
            using (var path = new GraphicsPath())
            {
                float w = size * 0.68f;
                float h = size;
                float mx = x + (size - w) / 2f;
                path.AddArc(mx, y, w, w, 180, 180);
                path.AddArc(mx, y + h - w, w, w, 0, 180);
                path.CloseFigure();
                g.DrawPath(pen, path);

                float cx = mx + w / 2f;
                g.DrawLine(pen, cx, y + h * 0.22f, cx, y + h * 0.44f);
            }
        }

        static void DrawHeadphoneIcon(Graphics g, float x, float y, float size, Color color)
        {
            using (var pen = new Pen(color, 1.35f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                float arcMargin = size * 0.1f;
                float arcW = size - 2f * arcMargin;
                g.DrawArc(pen, x + arcMargin, y + size * 0.06f, arcW, arcW * 1.05f, 180, 180);

                float cupW = size * 0.24f;
                float cupH = size * 0.42f;
                float cupY = y + size * 0.50f;
                float lx = x + arcMargin;
                float rx = x + size - arcMargin - cupW;
                g.DrawRectangle(pen, lx, cupY, cupW, cupH);
                g.DrawRectangle(pen, rx, cupY, cupW, cupH);
            }
        }

        static void DrawLightningBolt(Graphics g, float x, float y, float h, Color color)
        {
            using (var brush = new SolidBrush(color))
            {
                PointF[] pts = new PointF[]
                {
                    new PointF(x + h * 0.45f, y),
                    new PointF(x + h * 0.05f, y + h * 0.55f),
                    new PointF(x + h * 0.38f, y + h * 0.55f),
                    new PointF(x + h * 0.20f, y + h),
                    new PointF(x + h * 0.72f, y + h * 0.42f),
                    new PointF(x + h * 0.38f, y + h * 0.42f)
                };
                g.FillPolygon(brush, pts);
            }
        }

        void RenderWidget()
        {
            if (Width <= 0 || Height <= 0 || !IsHandleCreated) return;

            using (var bmp = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb))
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(1, 0, 0, 0)); // alpha=1 allows right-click hit-testing while visually 100% transparent
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

                float rowH = Height / 2f;
                float iconSize = 12f;
                float fontSize = 8.6f;

                using (var font = new Font("Segoe UI", fontSize, FontStyle.Regular))
                {
                    // Row 1: Mouse
                    Color mCol = GetStatusColor(_mouse);
                    float y1 = (rowH - iconSize) / 2f + 2f;
                    DrawMouseIcon(g, 4f, y1, iconSize, mCol);
                    string mStr = _mouse.Connected ? (_mouse.Percent + "%") : "--%";
                    using (var mb = new SolidBrush(mCol))
                    {
                        g.DrawString(mStr, font, mb, 20f, y1 - 2.2f);
                    }
                    if (_mouse.Connected && _mouse.Charging)
                    {
                        float textW = g.MeasureString(mStr, font).Width;
                        DrawLightningBolt(g, 19f + textW, y1 + 1f, 10f, mCol);
                    }

                    // Row 2: Headset
                    Color hCol = GetStatusColor(_headset);
                    float y2 = rowH + (rowH - iconSize) / 2f - 2f;
                    DrawHeadphoneIcon(g, 4f, y2, iconSize, hCol);
                    string hStr;
                    if (!_headset.Connected) hStr = "--%";
                    else if (_headset.Charging && _headset.Percent < 0) hStr = "Şarj";
                    else hStr = _headset.Percent + "%";
                    using (var hb = new SolidBrush(hCol))
                    {
                        g.DrawString(hStr, font, hb, 20f, y2 - 2.2f);
                    }
                    if (_headset.Connected && _headset.Charging)
                    {
                        float textW = g.MeasureString(hStr, font).Width;
                        DrawLightningBolt(g, 19f + textW, y2 + 1f, 10f, hCol);
                    }
                }

                ApplyLayeredBitmap(bmp);
            }
        }

        void ApplyLayeredBitmap(Bitmap bitmap)
        {
            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memDc = CreateCompatibleDC(screenDc);
            IntPtr hBitmap = IntPtr.Zero;
            IntPtr oldBitmap = IntPtr.Zero;
            try
            {
                hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
                oldBitmap = SelectObject(memDc, hBitmap);

                var size = new SIZE { cx = bitmap.Width, cy = bitmap.Height };
                var pointSource = new POINT { X = 0, Y = 0 };
                var topPos = new POINT { X = Left, Y = Top };
                var blend = new BLENDFUNCTION
                {
                    BlendOp = 0x00, // AC_SRC_OVER
                    BlendFlags = 0x00,
                    SourceConstantAlpha = 255,
                    AlphaFormat = 0x01 // AC_SRC_ALPHA
                };

                UpdateLayeredWindow(Handle, screenDc, ref topPos, ref size, memDc, ref pointSource, 0, ref blend, 0x02);
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screenDc);
                if (hBitmap != IntPtr.Zero)
                {
                    SelectObject(memDc, oldBitmap);
                    DeleteObject(hBitmap);
                }
                DeleteDC(memDc);
            }
        }
    }

    static class Program
    {
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main()
        {
            bool createdNew;
            using (var mutex = new Mutex(true, "TaskbarBatteryWidget_SingleInstance", out createdNew))
            {
                if (!createdNew) return;
                SetProcessDPIAware();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new BatteryWidgetForm());
            }
        }
    }
}
