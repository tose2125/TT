using System;
using System.Collections.Generic;
using System.IO;
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

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
            WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
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
        /// 生記録に追加する閾値
        /// </summary>
        private readonly TimeSpan _recordThreshold;

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
            _recordThreshold = TimeSpan.FromSeconds(1);
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
        }

        public void Stop()
        {
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
            lock (_lock)
            {
                _active.EndTime = DateTime.Now;
                if (_recordThreshold <= _active.Duration)
                {
                    _records.AddFirst(_active);
                }

                _active = new History
                {
                    Name = GetActiveWindowTitle(),
                    StartTime = DateTime.Now,
                    EndTime = DateTime.Now,
                };
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

            lock (_lock)
            {
                var newTitle = GetActiveWindowTitle();

                // タイトルが変わっていない場合は無視（同じウィンドウ内の別オブジェクトの名前変更など）
                if (newTitle == _active.Name)
                {
                    return;
                }

                _active.EndTime = DateTime.Now;
                if (_recordThreshold <= _active.Duration)
                {
                    _records.AddFirst(_active);
                }

                _active = new History
                {
                    Name = newTitle,
                    StartTime = DateTime.Now,
                    EndTime = DateTime.Now,
                };
            }
        }

        /// <summary>
        /// 毎分タイマーで呼び出されるイベントハンドラ
        /// </summary>
        private void OnTimerEveryMinute(object sender, ElapsedEventArgs e)
        {
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

                // 後ろの要素を削除
                var lastNode = _records.Find(recordToAddToHistories);
                if (lastNode != null)
                {
                    lastNode = lastNode.Next;
                    if (lastNode != null)
                    {
                        // lastNode より後ろをすべて削除する
                        while (_records.Last != lastNode)
                        {
                            _records.RemoveLast(); // 末尾から順番に消していく
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

                try
                {
                    ExportHistoryToCsv();
                }
                catch (Exception ex)
                {
                    // CSV 書き出しで例外が出てもトラッカー自体は継続させる
                    Console.Error.WriteLine(ex.Message);
                }
            }
        }

        private void ExportHistoryToCsv()
        {
            const string csvPath = "histories.csv";
            var filePath = Path.Combine(Environment.CurrentDirectory, csvPath);

            // 書き出し対象（ヘッダー）
            const string header = "date,start_time,end_time,event_type,event_value";

            // 書き出し対象（直近の履歴）
            var last = _histories.Last();
            var date = last.StartTime.ToString("yyyy-MM-dd");
            var startTime = last.StartTime.ToString("HH:mm");
            var endTime = last.EndTime.ToString("HH:mm");
            const string eventType = "window";
            var eventValueRaw = last.Name ?? string.Empty;
            // CSV の値は二重引用符で囲み、内部の二重引用符は二重化
            var eventValueEscaped = "\"" + eventValueRaw.Replace("\"", "\"\"") + "\"";
            var newLine = string.Format("{0},{1},{2},{3},{4}", date, startTime, endTime, eventType, eventValueEscaped);

            using (var fileStream =
                   new FileStream(filePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                var lines = new List<string>();
                using (var reader = new StreamReader(
                           fileStream,
                           Encoding.UTF8,
                           detectEncodingFromByteOrderMarks: true,
                           bufferSize: 1024,
                           leaveOpen: true))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        lines.Add(line);
                    }
                }

                if (lines.Count == 0)
                {
                    using (var writer = new StreamWriter(fileStream, Encoding.UTF8))
                    {
                        writer.WriteLine(header);
                        writer.WriteLine(newLine);
                    }

                    return;
                }

                // 最終有効行（空行はスキップ）
                var result = lines.Select((line, index) => new { Line = line, Index = index }).Reverse()
                    .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.Line));
                var lastLine = result != null ? result.Line : null;
                var lastLineNumber = result != null ? result.Index : -1;

                // 既存の最終行がヘッダーのみの場合は追記にフォールバック
                var hasHeader = lines.Count > 0 && lines[0].StartsWith("date,");

                // 既存の最終行から date,start_time,event_value を取得
                string lastDate = null, lastStartTime = null, lastEventType = null, lastValue = null;
                if (!string.IsNullOrEmpty(lastLine) && (!hasHeader || lastLine != lines[0]))
                {
                    var columns = lastLine.Split(',');
                    if (columns.Length >= 5)
                    {
                        lastDate = columns[0];
                        lastStartTime = columns[1];
                        // lastEndTime = columns[2];
                        lastEventType = columns[3];
                        var valPart = string.Join(",", columns.Skip(4)).Trim();
                        // 先頭と末尾の二重引用符を外し、内部の二重引用符を元に戻す
                        if (valPart.Length >= 2 && valPart[0] == '\"' &&
                            valPart[valPart.Length - 1] == '\"')
                        {
                            valPart = valPart.Substring(1, valPart.Length - 2).Replace("\"\"", "\"");
                        }

                        lastValue = valPart;
                    }
                }

                var isSameKey =
                    string.Equals(lastDate, date, StringComparison.Ordinal) &&
                    string.Equals(lastStartTime, startTime, StringComparison.Ordinal) &&
                    string.Equals(lastEventType, eventType, StringComparison.Ordinal) &&
                    string.Equals(lastValue, eventValueRaw, StringComparison.Ordinal);

                if (isSameKey && lastLineNumber >= 0)
                {
                    // end_time を更新（行を置き換え）
                    var position = fileStream.Length - 1;
                    var foundNewLine = false;

                    // 1. 末尾から逆方向に1バイトずつ読み、改行コード（\n）を探す
                    while (position >= 0)
                    {
                        fileStream.Position = position;
                        var b = fileStream.ReadByte();

                        // 完全に末尾にある改行は無視するため、position < fs.Length - 1 の条件を入れる
                        if (b == '\n' && position < fileStream.Length - 1)
                        {
                            // 改行コードの次の位置が「最後の行の先頭」
                            fileStream.Position = position + 1;
                            foundNewLine = true;
                            break;
                        }

                        position--;
                    }

                    // ファイルに改行が一つもなかった場合は、ファイルの先頭（0）が最後の行の先頭になる
                    if (!foundNewLine)
                    {
                        fileStream.Position = 0;
                    }

                    // 2. 新しい行のデータを書き込む
                    var startWritePosition = fileStream.Position; // 書き換え開始位置を記憶
                    var buffer = Encoding.UTF8.GetBytes(newLine + Environment.NewLine);
                    fileStream.Write(buffer, 0, buffer.Length);

                    // 3. 古い行の残骸を消す
                    // 新しい行の方が短かった場合、古い文字が残ってしまうのを防ぐ
                    fileStream.SetLength(startWritePosition + buffer.Length);
                }
                else
                {
                    // 追記
                    using (var writer = new StreamWriter(fileStream, Encoding.UTF8))
                    {
                        writer.BaseStream.Seek(0, SeekOrigin.End);
                        writer.WriteLine(newLine);
                    }
                }
            }
        }

        private static string GetActiveWindowTitle()
        {
            var foregroundWindow = GetForegroundWindow();
            var length = GetWindowTextLength(foregroundWindow);
            if (length == 0)
            {
                return string.Empty;
            }

            var sb = new StringBuilder(length + 1);
            GetWindowText(foregroundWindow, sb, sb.Capacity);
            return sb.ToString();
        }
    }

    /// <summary>
    /// アクティブウィンドウの履歴・記録
    /// </summary>
    internal struct History : IEquatable<History>
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

        public bool Equals(History other)
        {
            return Name == other.Name && StartTime.Equals(other.StartTime) && EndTime.Equals(other.EndTime);
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

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern sbyte GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool TranslateMessage([In] ref MSG lpMsg);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr DispatchMessage([In] ref MSG lpMsg);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
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