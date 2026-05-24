using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TT
{
    public class Program
    {
        public static void Main(string[] args)
        {
        }
    }

    /// <summary>
    /// アクティブウィンドウのタイムトラッカー
    /// </summary>
    internal class WindowTT
    {
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

        internal WindowTT()
        {
            _records = new LinkedList<History>();
            _histories = new List<History>();
            _threshold = TimeSpan.FromMinutes(1);
            _timerFrequency = TimeSpan.FromMinutes(1);
        }

        /// <summary>
        /// アクティブウィンドウが切り替わったら呼び出されるイベントハンドラ
        /// </summary>
        private void OnActiveWindowChanged()
        {
            _active.EndTime = DateTime.Now;
            _records.AddFirst(_active);
            _active = new History
            {
                Name = "",
                StartTime = DateTime.Now,
                EndTime = DateTime.Now,
            };
        }

        /// <summary>
        /// 毎分タイマーで呼び出されるイベントハンドラ
        /// </summary>
        private void OnTimerEveryMinute()
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
            if (_histories.Last().Name == recordToAddToHistories.Name)
            {
                var history = _histories.Last();
                history.EndTime += _timerFrequency;
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
}