using System.Text;
using AutomataFlow.Application.RecordFiles;
using AutomataFlow.Application.Workflows.Records;

namespace AutomataFlow.UnitTests.Application.Workflows;

/// <summary>Phase 5 step 4: streaming CSV/JSON readers with limits (no values in errors).</summary>
public class RecordImportTests
{
    private static readonly ImportLimits Limits = new() { MaxBytes = 1_000_000, MaxRecords = 100, MaxColumns = 5, MaxCellChars = 50 };

    private static async Task<List<ImportedRow>> CsvAsync(string text, ImportLimits? limits = null, byte[]? raw = null)
    {
        var rows = new List<ImportedRow>();
        await foreach (var row in RecordImport.ReadCsvAsync(new MemoryStream(raw ?? Encoding.UTF8.GetBytes(text)), limits ?? Limits))
        {
            rows.Add(row);
        }

        return rows;
    }

    private static async Task<List<ImportedRow>> JsonAsync(string text, ImportLimits? limits = null)
    {
        var rows = new List<ImportedRow>();
        await foreach (var row in RecordImport.ReadJsonAsync(new MemoryStream(Encoding.UTF8.GetBytes(text)), limits ?? Limits))
        {
            rows.Add(row);
        }

        return rows;
    }

    private static async Task<string> FailCsvAsync(string text, ImportLimits? limits = null, byte[]? raw = null) =>
        (await Assert.ThrowsAsync<ImportException>(() => CsvAsync(text, limits, raw))).Code;

    [Fact]
    public async Task Rfc4180_quoting_line_ends_and_empty_cells_are_handled()
    {
        var rows = await CsvAsync("name,note\r\n\"Doe, Jane\",\"said \"\"hi\"\"\"\n\"multi\nline\",\rplain,x\r\n\r\n");
        Assert.Equal(3, rows.Count);
        Assert.Equal(("Doe, Jane", "said \"hi\""), (rows[0].Values["name"], rows[0].Values["note"]));
        Assert.Equal("multi\nline", rows[1].Values["name"]);
        Assert.Null(rows[1].Values["note"]); // empty cell = absent value
        Assert.Equal((3, "plain", "x"), (rows[2].RowNumber, rows[2].Values["name"], rows[2].Values["note"]));
    }

    [Fact]
    public async Task A_utf8_bom_is_not_part_of_the_first_header()
    {
        var rows = await CsvAsync(string.Empty, raw: [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("email\na@example.test\n")]);
        Assert.Equal("a@example.test", rows.Single().Values["email"]);
    }

    [Theory]
    [InlineData("a,b\n1,2,3\n", "column-count-mismatch")]
    [InlineData("a,b\n1\"x,2\n", "quote-invalid")]
    [InlineData("a,b\n\"1\"x,2\n", "quote-invalid")]
    [InlineData("a,b\n\"unterminated,2\n", "quote-unterminated")]
    [InlineData("a,a\n1,2\n", "header-duplicate")]
    [InlineData("a, b\n1,2\n", "header-invalid")]
    [InlineData("a,\n1,2\n", "header-invalid")]
    [InlineData("", "header-missing")]
    [InlineData("a,b,c,d,e,f\n1,2,3,4,5,6\n", "too-many-columns")]
    public async Task Malformed_files_are_rejected_with_a_code(string csv, string code)
    {
        Assert.Equal(code, await FailCsvAsync(csv));
    }

    [Fact]
    public async Task Limits_are_enforced_while_streaming()
    {
        Assert.Equal("cell-too-long", await FailCsvAsync("a\n" + new string('x', 51) + "\n"));
        Assert.Equal("too-many-records", await FailCsvAsync("a\n" + string.Concat(Enumerable.Repeat("1\n", 101))));
        Assert.Equal("file-too-large", await FailCsvAsync("a\n" + string.Concat(Enumerable.Repeat("1\n", 60)), new ImportLimits { MaxBytes = 100, MaxRecords = 1000, MaxColumns = 5, MaxCellChars = 50 }));
    }

    [Fact]
    public async Task Invalid_utf8_is_refused_and_the_message_never_contains_cell_values()
    {
        var error = await Assert.ThrowsAsync<ImportException>(() => CsvAsync(string.Empty, raw: [.. Encoding.UTF8.GetBytes("a\nsecret-value-"), 0xC3, 0x28, (byte)'\n']));
        Assert.Equal("encoding-invalid", error.Code);

        var tooLong = await Assert.ThrowsAsync<ImportException>(() => CsvAsync("a\n" + "SSN-" + new string('9', 60) + "\n"));
        Assert.DoesNotContain("SSN", tooLong.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Json_arrays_of_flat_objects_are_read_with_scalars_as_text()
    {
        var rows = await JsonAsync("""[{"email":"a@example.test","count":42,"vip":true,"note":null},{"email":"b@example.test"}]""");
        Assert.Equal(2, rows.Count);
        Assert.Equal(("42", "true"), (rows[0].Values["count"], rows[0].Values["vip"]));
        Assert.Null(rows[0].Values["note"]);
    }

    [Theory]
    [InlineData("""{"email":"a"}""", "json-invalid")]
    [InlineData("""[{"a":{"nested":1}}]""", "value-not-scalar")]
    [InlineData("""[1,2]""", "record-not-object")]
    [InlineData("""[{"a":"1","a":"2"}]""", "duplicate-field")]
    [InlineData("""[{"a":"1"},""", "json-invalid")]
    public async Task Malformed_json_is_rejected_with_a_code(string json, string code)
    {
        Assert.Equal(code, (await Assert.ThrowsAsync<ImportException>(() => JsonAsync(json))).Code);
    }
}
