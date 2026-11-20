using System.Text.Json;
using AutomataFlow.AutomationTests.DemoCrm;
using AutomataFlow.AutomationTests.Support;
using AutomataFlow.Domain.Jobs;
using Microsoft.EntityFrameworkCore;

namespace AutomataFlow.AutomationTests.Platform;

/// <summary>
/// Phase 9: the shipped workflow packages (repository folder packages/) run with their OWN sample files through the real
/// Worker (PostgreSQL journal, real Chromium, egress proxy) against the DemoCrm back office, including their simulated
/// failures. The target's own counters prove that nothing was submitted or approved twice.
/// </summary>
[Collection(BrowserTestGroup.Name)]
public sealed class WorkflowPackageTests(BrowserFixture fixture)
{
    private static string Package(string id, string file) => File.ReadAllText(Path.Combine(DemoCrmProcess.RepositoryRoot, "packages", id, file));

    private static async Task<Dictionary<int, JobRecord>> RecordsAsync(PlatformRig rig, Guid job) =>
        (await rig.QueryAsync(db => db.JobRecords.AsNoTracking().Where(r => r.JobId == job).ToListAsync())).ToDictionary(r => r.RowNumber);

    private static string? Result(JobRecord record, string field) =>
        record.ResultJson is null ? null : JsonDocument.Parse(record.ResultJson).RootElement.TryGetProperty(field, out var value) ? value.GetString() : null;

    private static bool Ended(JobRecordStatus status) => status is JobRecordStatus.Succeeded or JobRecordStatus.Reconciled;

    [Fact]
    public async Task The_order_entry_package_places_each_order_once_and_reports_business_refusals()
    {
        await fixture.DemoCrm.ResetAsync();
        await using var rig = await PlatformRig.CreateAsync(fixture);
        var version = await rig.PublishPackageAsync(Package("ecommerce-order-entry", "workflow.json"), "shopPassword", "orders");
        var job = await rig.CreateJobAsync(version, Package("ecommerce-order-entry", "sample.csv"),
            new Dictionary<string, string> { ["shopBaseUrl"] = fixture.DemoCrm.BaseUrl, ["shopUser"] = "agent@democrm.test" }, new JobOptions(2, 3, 1));

        await rig.StartWorkerAsync();
        Assert.Equal(JobStatus.CompletedWithErrors, await rig.WaitForJobAsync(job, TimeSpan.FromMinutes(4)));
        var records = await RecordsAsync(rig, job);

        // Rows 1-3 plain orders, row 6 fails twice before succeeding, row 7 commits then reports an error.
        foreach (var row in new[] { 1, 2, 3, 6, 7 })
        {
            Assert.True(Ended(records[row].Status), $"row {row}: {records[row].Status} {records[row].ErrorCode}");
        }

        Assert.Equal("30.00", Result(records[1], "total")); // 2 x 12.50 + 5.00 standard shipping
        Assert.StartsWith("SO-", Result(records[2], "orderNumber"), StringComparison.Ordinal);
        Assert.Equal((JobRecordStatus.Failed, "OrderRejected"), (records[4].Status, records[4].ErrorCode)); // 5 x GADGET-X: out of stock
        Assert.Equal((JobRecordStatus.Failed, "OrderRejected"), (records[5].Status, records[5].ErrorCode)); // unknown customer
        Assert.True(records[6].AttemptCount >= 3, "the fail-twice row needed retries");

        // Exactly five orders exist: every retry and the commit-then-fail row were absorbed by the order reference.
        Assert.Equal(5, await fixture.DemoCrm.StateAsync("orderCount"));
    }

    [Fact]
    public async Task The_invoice_package_approves_each_matching_invoice_exactly_once()
    {
        await fixture.DemoCrm.ResetAsync();
        await using var rig = await PlatformRig.CreateAsync(fixture);
        var version = await rig.PublishPackageAsync(Package("invoice-approval", "workflow.json"), "backOfficePassword", "invoices");
        var job = await rig.CreateJobAsync(version, Package("invoice-approval", "sample.csv"),
            new Dictionary<string, string> { ["backOfficeUrl"] = fixture.DemoCrm.BaseUrl, ["backOfficeUser"] = "agent@democrm.test" }, new JobOptions(2, 3, 1));

        await rig.StartWorkerAsync();
        Assert.Equal(JobStatus.CompletedWithErrors, await rig.WaitForJobAsync(job, TimeSpan.FromMinutes(4)));
        var records = await RecordsAsync(rig, job);

        // INV-1001, -1002, -1005 approve normally; -1006 fails once before committing; -1007 commits, then reports an error.
        foreach (var row in new[] { 1, 2, 5, 6, 7 })
        {
            Assert.True(Ended(records[row].Status), $"row {row}: {records[row].Status} {records[row].ErrorCode}");
        }

        Assert.StartsWith("APR-", Result(records[1], "approvalReference"), StringComparison.Ordinal);
        Assert.Equal(("true", "APR-4999"), (Result(records[3], "alreadyApproved"), Result(records[3], "existingApprovalReference"))); // approved before
        Assert.True(Ended(records[3].Status));
        Assert.Equal((JobRecordStatus.Failed, "AmountMismatch"), (records[4].Status, records[4].ErrorCode)); // 75.10 invoiced, 75.00 expected
        Assert.Equal("75.10", Result(records[4], "actualAmount"));
        Assert.Equal((JobRecordStatus.Failed, "InvoiceNotFound"), (records[8].Status, records[8].ErrorCode));

        // Five new approvals in the target, no more: the retried and the commit-then-fail invoices were approved once.
        Assert.Equal(5, await fixture.DemoCrm.StateAsync("approvalCount"));
        var screenshots = await rig.QueryAsync(db => db.ExecutionArtifacts.CountAsync(a => a.Kind == "Screenshot"));
        Assert.True(screenshots >= 4, $"approval evidence screenshots: {screenshots}");
    }
}
