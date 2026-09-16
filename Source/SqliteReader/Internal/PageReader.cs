namespace SqliteReader.Internal;

/// <summary>
/// Reads pages for a single cursor. It uses an adaptive read-ahead window, so sequential
/// scans (the common case for leaf pages) need few system calls. Not thread-safe.
/// </summary>
internal sealed class PageReader
{
    private const int MaxWindowBytes = 1 << 20;

    private readonly DatabaseFile _file;
    private readonly int _maxWindowPages;
    private byte[] _window = [];
    private uint _windowFirst;
    private int _windowPages;
    private int _windowSize = 1;

    public PageReader(DatabaseFile file)
    {
        _file = file;
        _maxWindowPages = Math.Max(1, MaxWindowBytes / file.PageSize);
    }

    public DatabaseFile File => _file;

    public async ValueTask ReadPageAsync(uint pageNumber, byte[] destination, CancellationToken cancellationToken)
    {
        _file.ValidatePageNumber(pageNumber);
        int pageSize = _file.PageSize;

        if (_windowPages == 0 || pageNumber < _windowFirst || pageNumber >= _windowFirst + (uint)_windowPages)
        {
            bool sequential = _windowPages > 0 && pageNumber >= _windowFirst &&
                              pageNumber <= _windowFirst + (uint)_windowPages + 8;
            _windowSize = sequential ? Math.Min(_windowSize * 2, _maxWindowPages) : 1;

            int count = (int)Math.Min(_windowSize, _file.PageCount - pageNumber + 1L);
            if (_window.Length < count * pageSize)
            {
                _window = new byte[Math.Min(_windowSize * 2, _maxWindowPages) * pageSize];
            }

            _windowPages = 0;
            await _file.ReadPagesAsync(pageNumber, _window.AsMemory(0, count * pageSize), cancellationToken)
                .ConfigureAwait(false);
            _windowFirst = pageNumber;
            _windowPages = count;
        }

        Buffer.BlockCopy(_window, (int)(pageNumber - _windowFirst) * pageSize, destination, 0, pageSize);
    }
}
