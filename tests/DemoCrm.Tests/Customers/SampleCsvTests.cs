using DemoCrm.Customers;

namespace DemoCrm.Tests.Customers;

/// <summary>
/// samples/demo-customers.csv is the input for the first end-to-end AutomataFlow demonstration.
/// Each row declares its expected outcome; this test proves the labels are true for the real portal rules.
/// </summary>
public class SampleCsvTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    private sealed record Row(int Line, CustomerInput Input, string Expected);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AutomataFlow.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root (AutomataFlow.slnx).");
    }

    private static List<Row> LoadRows()
    {
        var path = Path.Combine(FindRepositoryRoot(), "samples", "demo-customers.csv");
        var lines = File.ReadAllLines(path);
        Assert.Equal("firstName,lastName,email,phone,company,country,customerType,expected", lines[0]);

        var rows = new List<Row>();
        for (var i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                continue;
            }

            var c = lines[i].Split(',');
            Assert.Equal(8, c.Length);
            rows.Add(new Row(
                i + 1,
                new CustomerInput
                {
                    FirstName = c[0],
                    LastName = c[1],
                    Email = c[2],
                    Phone = c[3],
                    Company = c[4],
                    Country = c[5],
                    CustomerType = c[6],
                    AcceptTerms = true,
                },
                c[7]));
        }

        return rows;
    }

    [Fact]
    public void Every_row_behaves_as_its_expected_label_says_when_processed_in_order()
    {
        var store = new InMemoryCustomerStore();

        foreach (var row in LoadRows())
        {
            var failures = CustomerInputValidator.Validate(row.Input, out var customer);
            string actual;
            if (customer is null)
            {
                actual = "invalid";
            }
            else
            {
                actual = store.Register(customer, Guid.NewGuid().ToString("D"), Now).Status switch
                {
                    RegistrationStatus.Created => "created",
                    RegistrationStatus.DuplicateEmail => "duplicate-email",
                    RegistrationStatus.DuplicatePhone => "duplicate-phone",
                    var other => other.ToString(),
                };
            }

            Assert.True(
                row.Expected == actual,
                $"Line {row.Line}: expected '{row.Expected}' but got '{actual}' ({string.Join("; ", failures.Select(f => f.Message))}).");
        }
    }

    [Fact]
    public void Sample_covers_every_outcome_category_and_every_failure_scenario()
    {
        var rows = LoadRows();

        foreach (var label in new[] { "created", "invalid", "duplicate-email", "duplicate-phone" })
        {
            Assert.Contains(rows, r => r.Expected == label);
        }

        foreach (var tag in new[] { "+fail-2@", "+commit-fail@", "+slow@" })
        {
            Assert.Contains(rows, r => r.Input.Email.Contains(tag, StringComparison.Ordinal));
        }

        // Fictional data only: every well-formed address uses the reserved .test domain.
        Assert.All(
            rows.Where(r => r.Expected != "invalid"),
            r => Assert.EndsWith("@example.test", r.Input.Email, StringComparison.Ordinal));
    }
}
