using System.Collections.Concurrent;
using Microsoft.UI.Dispatching;
using test.Models;

namespace test.Helpers;

/// <summary>
/// Animates the status text (e.g., "Downloading...") for download items.
/// </summary>
public sealed class DownloadItemStatusAnimator
{
    private readonly DispatcherQueue _dispatcher;

    private readonly ConcurrentDictionary<string, DispatcherQueueTimer> _timers = new();
    private readonly ConcurrentDictionary<string, int> _dots = new();
    private readonly ConcurrentDictionary<string, string> _baseText = new();

    private const int INTERVAL_MS = 500;

    public DownloadItemStatusAnimator(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public void Start(DownloadItem item, string baseText, int intervalMs = INTERVAL_MS)
    {
        if (item is null) throw new ArgumentNullException(nameof(item));

        void StartCore()
        {
            _baseText[item.ProductId] = baseText;

            var timer = _timers.GetOrAdd(item.ProductId, _ =>
            {
                _dots[item.ProductId] = 0;
                var t = _dispatcher.CreateTimer();
                t.Interval = TimeSpan.FromMilliseconds(intervalMs);
                t.Tick += (_, __) => Tick(item);
                return t;
            });

            timer.Start();
            UpdateItem(item);
        }

        if (_dispatcher.HasThreadAccess) StartCore();
        else _dispatcher.TryEnqueue(StartCore);
    }

    public void UpdateBase(DownloadItem item, string baseText)
    {
        if (item is null) throw new ArgumentNullException(nameof(item));

        void UpdateCore()
        {
            _baseText[item.ProductId] = baseText;
            
            if (_timers.ContainsKey(item.ProductId))
            {
                UpdateItem(item);
            }
        }

        if (_dispatcher.HasThreadAccess) UpdateCore();
        else _dispatcher.TryEnqueue(UpdateCore);
    }

    public void Stop(DownloadItem? item)
    {
        if (item is null) return;

        void StopCore()
        {
            if (_timers.TryRemove(item.ProductId, out var t))
            {
                t.Stop();
            }

            _dots.TryRemove(item.ProductId, out _);
            _baseText.TryRemove(item.ProductId, out _);
        }

        if (_dispatcher.HasThreadAccess) StopCore();
        else _dispatcher.TryEnqueue(StopCore);
    }

    private void Tick(DownloadItem item)
    {
        if (!_dots.TryGetValue(item.ProductId, out var d))
            d = 0;

        d = (d % 3) + 1;
        _dots[item.ProductId] = d;
        UpdateItem(item);
    }

    private void UpdateItem(DownloadItem item)
    {
        var baseText = _baseText.TryGetValue(item.ProductId, out var b) ? b : "";
        var dots = _dots.TryGetValue(item.ProductId, out var d) ? d : 0;

        var tail = dots switch
        {
            1 => ".  ",
            2 => ".. ",
            3 => "...",
            _ => "   "
        };

        item.StatusTextOverride = baseText + tail;
    }
}
