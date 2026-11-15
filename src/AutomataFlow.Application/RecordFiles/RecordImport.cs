using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace AutomataFlow.Application.RecordFiles;

/// <summary>Upload limits (configurable by the server; docs/ARCHITECTURE.md §8).</summary>
public sealed class ImportLimits
{
    public long MaxBytes { get; init; } = 10 * 1024 * 1024;

    public int MaxRecords { get; init; } = 10_000;

    public int MaxColumns { get; init; } = 100;

    public int MaxCellChars { get; init; } = 10_000;
}

/// <summary>A file-level import failure. Messages carry positions and codes, never cell values.</summary>
public sealed class ImportException : Exception
{
    public ImportException()
    {
    }

    public ImportException(string message)
        : base(message)
    {
    }

    public ImportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public ImportException(string code, string message, int? row = null)
        : base(message)
    {
        Code = code;
        Row = row;
    }

    public string Code { get; } = "import-invalid";

    /// <summary>1-based data row (after the CSV header), when the problem is in a row.</summary>
    public int? Row { get; }
}

/// <summary>One data row: its 1-based number and its values by column (empty cells are absent).</summary>
public sealed record ImportedRow(int RowNumber, IReadOnlyDictionary<string, string?> Values);

/// <summary>
/// Streaming record readers (Phase 5 step 4). Rows are produced one at a time, so memory is bounded by one row whatever
/// the file size; size, record, column and cell limits are enforced while reading. UTF-8 only (invalid bytes are refused).
/// </summary>
public static class RecordImport
{
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 4 };

    /// <summary>RFC 4180 CSV with a header row; CRLF/LF/CR line ends; quoted fields with doubled quotes; strict.</summary>
    public static async IAsyncEnumerable<ImportedRow> ReadCsvAsync(Stream utf8, ImportLimits limits, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(utf8);
        ArgumentNullException.ThrowIfNull(limits);
        using var reader = new StreamReader(new LimitedStream(utf8, limits.MaxBytes), new UTF8Encoding(false, throwOnInvalidBytes: true), detectEncodingFromByteOrderMarks: false, bufferSize: 16 * 1024);
        var parser = new CsvParser(reader, limits);
        string[]? header = null;
        var dataRow = 0;
        while (true)
        {
            List<string>? fields;
            try
            {
                fields = await parser.NextRecordAsync(cancellationToken);
            }
            catch (DecoderFallbackException)
            {
                throw new ImportException("encoding-invalid", "The file is not valid UTF-8.", dataRow + 1);
            }

            if (fields is null)
            {
                break;
            }

            if (header is null)
            {
                header = ValidateHeader(fields, limits);
                continue;
            }

            if (fields is [""] )
            {
                continue; // blank line
            }

            dataRow++;
            if (dataRow > limits.MaxRecords)
            {
                throw new ImportException("too-many-records", $"The file has more than {limits.MaxRecords} records.", dataRow);
            }

            if (fields.Count != header.Length)
            {
                throw new ImportException("column-count-mismatch", $"Row {dataRow} has {fields.Count} fields; the header has {header.Length}.", dataRow);
            }

            var values = new Dictionary<string, string?>(header.Length, StringComparer.Ordinal);
            for (var i = 0; i < header.Length; i++)
            {
                values[header[i]] = fields[i].Length == 0 ? null : fields[i];
            }

            yield return new ImportedRow(dataRow, values);
        }

        if (header is null)
        {
            throw new ImportException("header-missing", "The file has no header row.");
        }
    }

    /// <summary>A JSON array of flat objects whose values are strings, numbers, booleans or null.</summary>
    public static async IAsyncEnumerable<ImportedRow> ReadJsonAsync(Stream utf8, ImportLimits limits, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(utf8);
        ArgumentNullException.ThrowIfNull(limits);
        var row = 0;
        var enumerator = JsonSerializer.DeserializeAsyncEnumerable<JsonElement>(new LimitedStream(utf8, limits.MaxBytes), JsonOptions, cancellationToken).GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                try
                {
                    if (!await enumerator.MoveNextAsync())
                    {
                        break;
                    }
                }
                catch (JsonException)
                {
                    throw new ImportException("json-invalid", "The file is not a JSON array of objects.", row + 1);
                }

                row++;
                if (row > limits.MaxRecords)
                {
                    throw new ImportException("too-many-records", $"The file has more than {limits.MaxRecords} records.", row);
                }

                yield return new ImportedRow(row, ToValues(enumerator.Current, row, limits));
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }

    private static Dictionary<string, string?> ToValues(JsonElement element, int row, ImportLimits limits)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ImportException("record-not-object", $"Record {row} is not a JSON object.", row);
        }

        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (values.Count >= limits.MaxColumns)
            {
                throw new ImportException("too-many-columns", $"Record {row} has more than {limits.MaxColumns} fields.", row);
            }

            string? value = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number => property.Value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null => null,
                _ => throw new ImportException("value-not-scalar", $"Record {row} has a nested value.", row),
            };
            if (value is { Length: > 0 } && value.Length > limits.MaxCellChars)
            {
                throw new ImportException("cell-too-long", $"Record {row} has a value longer than {limits.MaxCellChars} characters.", row);
            }

            if (!values.TryAdd(property.Name, string.IsNullOrEmpty(value) ? null : value))
            {
                throw new ImportException("duplicate-field", $"Record {row} repeats a field.", row);
            }
        }

        return values;
    }

    private static string[] ValidateHeader(List<string> fields, ImportLimits limits)
    {
        if (fields.Count > limits.MaxColumns)
        {
            throw new ImportException("too-many-columns", $"The header has more than {limits.MaxColumns} columns.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in fields)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Trim().Length != name.Length)
            {
                throw new ImportException("header-invalid", "A header cell is empty or has surrounding whitespace.");
            }

            if (!seen.Add(name))
            {
                throw new ImportException("header-duplicate", "The header repeats a column name.");
            }
        }

        return [.. fields];
    }

    /// <summary>Strict CSV state machine over a text reader.</summary>
    private sealed class CsvParser(TextReader reader, ImportLimits limits)
    {
        private readonly char[] _buffer = new char[8192];
        private int _length;
        private int _position;
        private bool _eof;

        public async Task<List<string>?> NextRecordAsync(CancellationToken cancellationToken)
        {
            if (!await EnsureAsync(cancellationToken))
            {
                return null;
            }

            var fields = new List<string>();
            var cell = new StringBuilder();
            while (true)
            {
                if (!await EnsureAsync(cancellationToken))
                {
                    fields.Add(Take(cell));
                    return fields;
                }

                var c = _buffer[_position++];
                if (c == '"' && cell.Length == 0)
                {
                    await ReadQuotedAsync(cell, cancellationToken);
                    if (!await EnsureAsync(cancellationToken))
                    {
                        fields.Add(Take(cell));
                        return fields;
                    }

                    c = _buffer[_position++];
                    if (c is not (',' or '\r' or '\n'))
                    {
                        throw new ImportException("quote-invalid", "A quoted field is followed by other characters.");
                    }
                }
                else if (c == '"')
                {
                    throw new ImportException("quote-invalid", "A quote appears inside an unquoted field.");
                }
                else if (c is not (',' or '\r' or '\n'))
                {
                    Append(cell, c);
                    continue;
                }

                AddField(fields, cell);
                if (c == ',')
                {
                    continue;
                }

                if (c == '\r' && await EnsureAsync(cancellationToken) && _buffer[_position] == '\n')
                {
                    _position++;
                }

                return fields;
            }
        }

        private async Task ReadQuotedAsync(StringBuilder cell, CancellationToken cancellationToken)
        {
            while (true)
            {
                if (!await EnsureAsync(cancellationToken))
                {
                    throw new ImportException("quote-unterminated", "A quoted field is not closed.");
                }

                var c = _buffer[_position++];
                if (c != '"')
                {
                    Append(cell, c);
                    continue;
                }

                if (await EnsureAsync(cancellationToken) && _buffer[_position] == '"')
                {
                    _position++;
                    Append(cell, '"');
                    continue;
                }

                return;
            }
        }

        private void AddField(List<string> fields, StringBuilder cell)
        {
            if (fields.Count >= limits.MaxColumns)
            {
                throw new ImportException("too-many-columns", $"A row has more than {limits.MaxColumns} fields.");
            }

            fields.Add(Take(cell));
        }

        private void Append(StringBuilder cell, char c)
        {
            if (cell.Length >= limits.MaxCellChars)
            {
                throw new ImportException("cell-too-long", $"A value is longer than {limits.MaxCellChars} characters.");
            }

            cell.Append(c);
        }

        private static string Take(StringBuilder cell)
        {
            var value = cell.ToString();
            cell.Clear();
            return value;
        }

        private async Task<bool> EnsureAsync(CancellationToken cancellationToken)
        {
            if (_position < _length)
            {
                return true;
            }

            if (_eof)
            {
                return false;
            }

            _length = await reader.ReadAsync(_buffer.AsMemory(), cancellationToken);
            _position = 0;
            if (_length == 0)
            {
                _eof = true;
                return false;
            }

            // A UTF-8 BOM at the very start is not data.
            return true;
        }
    }

    /// <summary>Fails the read once more than <c>max</c> bytes were read (the declared size is not trusted).</summary>
    private sealed class LimitedStream(Stream inner, long max) : Stream
    {
        private long _read;
        private bool _bomChecked;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => _read; set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var n = await inner.ReadAsync(buffer, cancellationToken);
            if (!_bomChecked && n >= 3 && buffer.Span[0] == 0xEF && buffer.Span[1] == 0xBB && buffer.Span[2] == 0xBF)
            {
                // Drop a UTF-8 BOM: shift it out of the returned data.
                buffer[3..n].CopyTo(buffer);
                n -= 3;
                _read += 3;
            }

            _bomChecked = true;
            _read += n;
            if (_read > max)
            {
                throw new ImportException("file-too-large", $"The file is larger than {max} bytes.");
            }

            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
