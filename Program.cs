using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Timers;

namespace TT
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var tt = new WindowTT();
            tt.Start();
            Console.WriteLine("Press any key to exit...");

            // 別スレッドでキー入力を待ち、押されたらWM_QUITを送ってメッセージループを終了
            var keyWaiter = Task.Run(() =>
            {
                Console.ReadKey(true);
                NativeMethods.PostQuitMessage(0);
            });

            // Win32 APIのメッセージループを回す（WinEventフックのため）
            NativeMethods.MessageLoop();

            tt.Stop();
        }
    }

    /// <summary>
    /// アクティブウィンドウのタイムトラッカー
    /// </summary>
    internal class WindowTT : IDisposable
    {
        #region Win32 API

        private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject,
            int idChild, uint dwEventThread, uint dwmsEventTime);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
            WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

        #endregion

        private const uint EVENT_SYSTEM_FOREGROUND = 3;
        private const uint EVENT_OBJECT_NAMECHANGE = 0x800C;
        private const uint WINEVENT_OUTOFCONTEXT = 0;
        private const int OBJID_WINDOW = 0;

        /// <summary>
        /// 現在のアクティブウィンドウ
        /// </summary>
        private History _active;

        /// <summary>
        /// 秒単位の生の記録
        /// </summary>
        private readonly LinkedList<History> _records;

        /// <summary>
        /// 分単位の代表の履歴
        /// </summary>
        private readonly List<History> _histories;

        /// <summary>
        /// 代表とみなす閾値
        /// </summary>
        private readonly TimeSpan _threshold;

        /// <summary>
        /// タイマー周期
        /// </summary>
        private readonly TimeSpan _timerFrequency;

        /// <summary>
        /// タイマー
        /// </summary>
        private Timer _timer;

        /// <summary>
        /// マルチスレッドロック
        /// </summary>
        private readonly object _lock = new object();

        /// <summary>
        /// Win32 API EVENT_SYSTEM_FOREGROUND ハンドラ
        /// </summary>
        private IntPtr _hookHandleForeground;

        /// <summary>
        /// Win32 API EVENT_OBJECT_NAMECHANGE ハンドラ
        /// </summary>
        private IntPtr _hookHandleNameChange;

        /// <summary>
        /// コールバックデリゲートをフィールドに保持（GCで回収されないように）
        /// </summary>
        private readonly WinEventDelegate _eventProcForeground;

        /// <summary>
        /// コールバックデリゲートをフィールドに保持（GCで回収されないように）
        /// </summary>
        private readonly WinEventDelegate _eventProcNameChange;

        internal WindowTT()
        {
            _active = new History
            {
                Name = GetActiveWindowTitle(),
                StartTime = DateTime.Now,
                EndTime = DateTime.Now,
            };
            _records = new LinkedList<History>();
            _histories = new List<History>();
            _threshold = TimeSpan.FromMinutes(1);
            _timerFrequency = TimeSpan.FromMinutes(1);
            _timer = new Timer(_timerFrequency.TotalMilliseconds)
            {
                AutoReset = true
            };
            _timer.Elapsed += OnTimerEveryMinute;
            _eventProcForeground = OnActiveWindowChanged;
            _eventProcNameChange = OnWindowNameChanged;
        }

        public void Start()
        {
            _timer.Enabled = true;

            _hookHandleForeground = SetWinEventHook(
                EVENT_SYSTEM_FOREGROUND,
                EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero,
                _eventProcForeground,
                0,
                0,
                WINEVENT_OUTOFCONTEXT);

            _hookHandleNameChange = SetWinEventHook(
                EVENT_OBJECT_NAMECHANGE,
                EVENT_OBJECT_NAMECHANGE,
                IntPtr.Zero,
                _eventProcNameChange,
                0,
                0,
                WINEVENT_OUTOFCONTEXT);

            Console.WriteLine("[DEBUG] Window TT started");
        }

        public void Stop()
        {
            Console.WriteLine("[DEBUG] Window TT stopping");
            if (_hookHandleForeground != IntPtr.Zero)
            {
                UnhookWinEvent(_hookHandleForeground);
                _hookHandleForeground = IntPtr.Zero;
            }

            if (_hookHandleNameChange != IntPtr.Zero)
            {
                UnhookWinEvent(_hookHandleNameChange);
                _hookHandleNameChange = IntPtr.Zero;
            }

            if (_timer != null)
            {
                _timer.Stop();
                _timer.Dispose();
                _timer = null;
            }
        }

        public void Dispose()
        {
            Stop();
        }

        /// <summary>
        /// アクティブウィンドウが切り替わったら呼び出されるイベントハンドラ
        /// </summary>
        private void OnActiveWindowChanged(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild,
            uint dwEventThread, uint dwmsEventTime)
        {
            Console.WriteLine("[DEBUG] OnActiveWindowChanged");
            lock (_lock)
            {
                _active.EndTime = DateTime.Now;
                _records.AddFirst(_active);
                _active = new History
                {
                    Name = GetActiveWindowTitle(),
                    StartTime = DateTime.Now,
                    EndTime = DateTime.Now,
                };
                Console.WriteLine("[DEBUG] Active window changed: '{0}'", _active.Name);
            }
        }

        /// <summary>
        /// アクティブウィンドウのタイトルが変わったら呼び出されるイベントハンドラ
        /// </summary>
        private void OnWindowNameChanged(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild,
            uint dwEventThread, uint dwmsEventTime)
        {
            // 名前変更イベントの場合、ウィンドウオブジェクト以外は無視する
            if (idObject != OBJID_WINDOW)
            {
                return;
            }

            Console.WriteLine("[DEBUG] OnWindowNameChanged");
            lock (_lock)
            {
                var newTitle = GetActiveWindowTitle();

                // タイトルが変わっていない場合は無視（同じウィンドウ内の別オブジェクトの名前変更など）
                if (newTitle == _active.Name)
                {
                    return;
                }

                _active.EndTime = DateTime.Now;
                _records.AddFirst(_active);
                _active = new History
                {
                    Name = newTitle,
                    StartTime = DateTime.Now,
                    EndTime = DateTime.Now,
                };

                Console.WriteLine("[DEBUG] Active window name changed: {0}", newTitle);
            }
        }

        /// <summary>
        /// 毎分タイマーで呼び出されるイベントハンドラ
        /// </summary>
        private void OnTimerEveryMinute(object sender, ElapsedEventArgs e)
        {
            Console.WriteLine("[DEBUG] OnTimerEveryMinute");
            lock (_lock)
            {
                _active.EndTime = DateTime.Now;
                var recordToAddToHistories = _active;
                // ウィンドウごとの合計時間
                var windows = new Dictionary<string, TimeSpan>();
                windows.Add(_active.Name, _active.Duration);
                if (windows[_active.Name] < _threshold)
                {
                    foreach (var record in _records)
                    {
                        // 直近に開いたウィンドウから順に合計時間へ加算
                        TimeSpan duration;
                        if (windows.TryGetValue(record.Name, out duration))
                        {
                            windows[record.Name] = record.Duration + duration;
                        }
                        else
                        {
                            windows.Add(record.Name, record.Duration);
                        }

                        // いま加算したことにより閾値をクリアするか
                        if (_threshold <= windows[record.Name])
                        {
                            recordToAddToHistories = record;
                            break;
                        }
                    }
                }

                // 履歴へ追加
                if (_histories.Count > 0 && _histories.Last().Name == recordToAddToHistories.Name)
                {
                    var history = _histories.Last();
                    history.EndTime += _timerFrequency;
                    _histories[_histories.Count - 1] = history;
                }
                else
                {
                    var now = DateTime.Now;
                    var history = new History
                    {
                        Name = recordToAddToHistories.Name,
                        StartTime = now - _timerFrequency,
                        EndTime = now,
                    };
                    _histories.Add(history);
                }

                Console.WriteLine("[DEBUG] History updated: '{0}'", _histories.Last().Name);
            }
        }

        private static string GetActiveWindowTitle()
        {
            var foregroundWindow = GetForegroundWindow();
            var sb = new StringBuilder(256);
            GetWindowText(foregroundWindow, sb, 256);
            return sb.ToString();
        }
    }

    /// <summary>
    /// アクティブウィンドウの履歴・記録
    /// </summary>
    internal struct History
    {
        /// <summary>
        /// ウィンドウの名前
        /// </summary>
        internal string Name { get; set; }

        /// <summary>
        /// ウィンドウを開いた時刻
        /// </summary>
        internal DateTime StartTime { get; set; }

        /// <summary>
        /// ウィンドウを閉じた時刻
        /// </summary>
        internal DateTime EndTime { get; set; }

        /// <summary>
        /// ウィンドウを開いていた時間
        /// </summary>
        internal TimeSpan Duration
        {
            get { return EndTime - StartTime; }
        }
    }

    /// <summary>
    /// Win32 メッセージループ（ConsoleアプリでWinEventフックを安定して受けるために使用）
    /// </summary>
    internal static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public UIntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
        }

        [DllImport("user32.dll")]
        private static extern sbyte GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage([In] ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage([In] ref MSG lpMsg);

        [DllImport("user32.dll")]
        internal static extern void PostQuitMessage(int nExitCode);

        internal static void MessageLoop()
        {
            MSG msg;
            // GetMessageはWM_QUITが来ると0を返す
            while (GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
    }
}