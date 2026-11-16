using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutomataFlow.AutomationTests.DemoCrm;
using AutomataFlow.AutomationTests.Support;
using AutomataFlow.Domain.Jobs;
using Microsoft.EntityFrameworkCore;

namespace AutomataFlow.AutomationTests.Platform;

/// <summary>
/// Phase 5 step 8a: the platform as separate OS processes (real Api, test-only Worker host with the real Worker
/// composition), the demo CSV end to end through the Api, and Workers that are REALLY killed (SIGKILL of the whole
/// process tree) before and after a side-effect checkpoint, with exact DemoCrm customer and POST counts. The process-death
/// supervisor is step 7 (not available here): an unfenced attempt therefore waits, becomes NeedsReview and is never
/// retried automatically; only an audited operator fence plus an explicit requeue resume it.
/// </summary>
[Collection(BrowserTestGroup.Name)]
public sealed class ProcessRecoveryTests(BrowserFixture fixture)
{
    private static readonly Dictionary<string, string?> Fast = new()
    {
        ["Journal:LeaseSeconds"] = "130",          // the minimum the Worker accepts: 120 s side-effect step + 10 s margin
        ["Worker:ClaimLeaseSeconds"] = "10",
        ["Worker:HeartbeatSeconds"] = "2",
        ["Worker:RecoveryIntervalSeconds"] = "2",
        ["Worker:FenceRecheckSeconds"] = "5",
        ["Worker:FenceWaitSeconds"] = "60",
    };

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<Guid> PublishAsync(HttpClient api, string definition)
    {
        var created = await JsonAsync(await api.PostAsJsonAsync("/api/v1/workflows", new { name = "wf-" + Guid.NewGuid().ToString("N")[..8], definition = JsonNode.Parse(definition) }));
        var (workflow, version) = (created.GetProperty("workflowId").GetGuid(), created.GetProperty("version").GetProperty("id").GetGuid());
        await JsonAsync(await api.PostAsJsonAsync($"/api/v1/workflows/{workflow}/versions/{version}/publish", new { acknowledgeAttestations = true }));
        Assert.Equal(HttpStatusCode.NoContent, (await api.PutAsJsonAsync("/api/v1/secrets/crmPassword", new { value = "DemoPass-2026!" })).StatusCode);
        return version;
    }

    private async Task<Guid> UploadAsync(HttpClient api, Guid version, string csv, bool ignoreUnknown = false, int concurrency = 2)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent(version.ToString()), "workflowVersionId" },
            { new StringContent(JsonSerializer.Serialize(new Dictionary<string, string> { ["crmBaseUrl"] = fixture.DemoCrm.BaseUrl, ["crmUser"] = "agent@democrm.test" })), "parameters" },
            { new StringContent($$"""{"maxConcurrency":{{concurrency}},"maxAttempts":3,"retryBackoffSeconds":1}"""), "options" },
            { new StringContent(ignoreUnknown ? "true" : "false"), "ignoreUnknownColumns" },
        };
        var file = new StringContent(csv);
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(file, "file", "records.csv");
        return (await JsonAsync(await api.PostAsync("/api/v1/jobs", form))).GetProperty("jobId").GetGuid();
    }

    private static async Task<string> WaitForJobAsync(HttpClient api, Guid job, TimeSpan timeout, PlatformProcesses processes)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var status = (await JsonAsync(await api.GetAsync($"/api/v1/jobs/{job}"))).GetProperty("status").GetString()!;
            if (status is not ("Pending" or "Running"))
            {
                return status;
            }

            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"Job still {status}. Records: {await api.GetStringAsync($"/api/v1/jobs/{job}/records")}{Environment.NewLine}"
                    + string.Join(Environment.NewLine, processes.Processes.Select(p => $"--- {p.Name} {p.Id}{Environment.NewLine}{p.Tail(40)}")));
            }

            await Task.Delay(500);
        }
    }

    [Fact]
    public async Task The_demo_csv_runs_end_to_end_through_the_Api_and_two_Worker_processes()
    {
        await using var rig = await PlatformRig.CreateAsync(fixture);
        await using var processes = new PlatformProcesses(rig);
        await fixture.DemoCrm.ResetAsync();
        var customers = await fixture.DemoCrm.CustomerCountAsync();
        await processes.StartApiAsync();
        await processes.StartWorkerAsync();
        await processes.StartWorkerAsync();

        var (token, _, _) = await processes.RegisterAsync();
        var api = processes.Client(token);
        var version = await PublishAsync(api, WorkflowRig.SampleJson);
        var csvText = await File.ReadAllTextAsync(Path.Combine(DemoCrmProcess.RepositoryRoot, "samples", "demo-customers.csv"));
        var job = await UploadAsync(api, version, csvText, ignoreUnknown: true);

        Assert.Equal("CompletedWithErrors", await WaitForJobAsync(api, job, TimeSpan.FromMinutes(5), processes));

        var expected = csvText.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(l => l.Trim().Split(',')[^1]).ToList();
        var records = (await JsonAsync(await api.GetAsync($"/api/v1/jobs/{job}/records?take=100"))).EnumerateArray().OrderBy(r => r.GetProperty("rowNumber").GetInt32()).ToList();
        Assert.Equal(expected.Count, records.Count);
        for (var i = 0; i < records.Count; i++)
        {
            var (status, code) = (records[i].GetProperty("status").GetString(), records[i].GetProperty("errorCode").ToString());
            switch (expected[i])
            {
                case "created":
                    Assert.True(status == "Succeeded", $"row {i + 1}: {status} {code}");
                    break;
                case "duplicate-email" or "duplicate-phone":
                    Assert.True(status == "Failed" && code == "DuplicateCustomer", $"row {i + 1}: {status} {code}");
                    break;
                default:
                    Assert.True(status is "Invalid" or "Failed", $"row {i + 1}: {status} {code}");
                    break;
            }
        }

        // Exactly the 7 "created" rows exist at the target, once each, although two Worker processes ran the job.
        Assert.Equal(customers + expected.Count(e => e == "created"), await fixture.DemoCrm.CustomerCountAsync());
        var results = await api.GetStringAsync($"/api/v1/jobs/{job}/results.csv");
        Assert.Equal(expected.Count(e => e == "created"), results.Split('\n').Count(l => l.Contains(",Succeeded,", StringComparison.Ordinal)));
        Assert.DoesNotContain("priya.raman@example.test", results, StringComparison.Ordinal); // exports never carry inputs
    }

    [Fact]
    public async Task Really_killed_workers_recover_safely_before_and_after_a_side_effect_checkpoint()
    {
        await using var rig = await PlatformRig.CreateAsync(fixture);
        await using var processes = new PlatformProcesses(rig);
        await processes.StartApiAsync();
        var (token, org, user) = await processes.RegisterAsync();
        await rig.GrantPlatformOperatorAsync(user);
        var api = processes.Client(token);

        // K2 workflow: the sample with a 20 s pause before the submit (a kill there provably precedes any side effect).
        var paused = JsonNode.Parse(WorkflowRig.SampleJson)!.AsObject();
        var steps = paused["steps"]!.AsArray();
        steps.Insert(steps.IndexOf(steps.Single(s => (string?)s!["id"] == "submit")), JsonNode.Parse("""{"id":"pause","type":"delay","config":{"milliseconds":20000}}"""));
        var beforeVersion = await PublishAsync(api, paused.ToJsonString());
        // K1 workflow: variant B (no idempotency, business-key reconcile): an ambiguous attempt can only be reconciled.
        var afterVersion = await PublishAsync(api, WorkflowRig.VariantB);

        var (customers, posts) = (await fixture.DemoCrm.CustomerCountAsync(), await fixture.DemoCrm.RegistrationSubmissionsAsync());
        var k1Email = $"k1{Guid.NewGuid():N}"[..18] + "+late-commit@example.test";
        var k2Job = await UploadAsync(api, beforeVersion, "firstName,lastName,email,phone,company,country,customerType\nAda,Lovelace,k2" + Guid.NewGuid().ToString("N")[..12] + "@example.test,+1 555 7101,Engines,Canada,Premium\n", concurrency: 1);
        var k1Job = await UploadAsync(api, afterVersion, $"firstName,lastName,email,phone,company,country,customerType\nGrace,Hopper,{k1Email},+1 555 7102,Compilers,Canada,Premium\n", concurrency: 1);

        var k2Op = await rig.QueryAsync(org, db => db.JobRecords.Where(r => r.JobId == k2Job).Select(r => r.OperationId).SingleAsync());
        var k1Op = await rig.QueryAsync(org, db => db.JobRecords.Where(r => r.JobId == k1Job).Select(r => r.OperationId).SingleAsync());
        var doomed = await processes.StartWorkerAsync(Fast);
        // Kill when K2's attempt is Started WITHOUT a checkpoint and K1's attempt HAS a checkpoint and its POST arrived.
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            var entries = await rig.QueryAsync(db => db.AttemptJournal.AsNoTracking().Where(e => e.OrganizationId == org).ToListAsync());
            var k2Started = entries.Any(e => e.OperationId == k2Op && e.State == "Started" && e.CheckpointAt == null);
            var k1Checkpointed = entries.Any(e => e.OperationId == k1Op && e.CheckpointAt != null) && await fixture.DemoCrm.RegistrationSubmissionsAsync() == posts + 1;
            if (k2Started && k1Checkpointed)
            {
                break;
            }

            Assert.True(DateTime.UtcNow < deadline, "the kill window was not reached: " + doomed.Tail());
            await Task.Delay(100);
        }

        await doomed.KillAsync(); // SIGKILL: Worker, egress proxy, Playwright driver and every Chromium process
        var killedAt = DateTime.UtcNow;
        await processes.StartWorkerAsync(Fast);

        // K2: abandoned WITHOUT checkpoint => provably no side effect (NoCheckpoint) => retried automatically, once.
        Assert.Equal("Completed", await WaitForJobAsync(api, k2Job, TimeSpan.FromMinutes(5), processes));

        // K1: abandoned WITH checkpoint and no proof of death => AwaitingFence, then NeedsReview; never retried.
        var record = await WaitForRecordAsync(api, k1Job, "NeedsReview", TimeSpan.FromMinutes(6), processes);
        Assert.Equal("FenceNotConfirmed", record.GetProperty("errorCode").GetString());
        Assert.True(DateTime.UtcNow - killedAt >= TimeSpan.FromSeconds(130), "nothing may move before the dead attempt's lease expired");
        Assert.Equal((customers + 2, posts + 2), (await fixture.DemoCrm.CustomerCountAsync(), await fixture.DemoCrm.RegistrationSubmissionsAsync()));
        // (K2 one POST; K1 its single POST, whose LATE commit happened after the Worker died: customers +2.)

        // Operator fence through the Api (audited), then an explicit requeue: the Worker reconciles and FINDS the late commit.
        var incarnations = (await JsonAsync(await api.GetAsync("/api/v1/platform/incarnations"))).EnumerateArray().ToList();
        var dead = incarnations.Single(i => i.GetProperty("unfencedAttempts").GetInt32() == 1).GetProperty("id").GetGuid();
        await JsonAsync(await api.PostAsJsonAsync($"/api/v1/platform/incarnations/{dead}/fence",
            new { method = "HostPoweredOffOrDestroyed", evidenceReference = "E2E-K1: worker process tree killed with SIGKILL by the test", attestedPersonally = true }));
        Assert.Equal(HttpStatusCode.Accepted, (await api.PostAsync($"/api/v1/jobs/{k1Job}/records/{record.GetProperty("id").GetGuid()}/requeue", null)).StatusCode);

        Assert.Equal("Completed", await WaitForJobAsync(api, k1Job, TimeSpan.FromMinutes(2), processes));
        var reconciled = (await JsonAsync(await api.GetAsync($"/api/v1/jobs/{k1Job}/records"))).EnumerateArray().Single();
        Assert.Equal("Reconciled", reconciled.GetProperty("status").GetString());
        Assert.Equal((customers + 2, posts + 2), (await fixture.DemoCrm.CustomerCountAsync(), await fixture.DemoCrm.RegistrationSubmissionsAsync()));

        var history = await rig.QueryAsync(db => db.AttemptJournal.AsNoTracking().Where(e => e.OrganizationId == org).OrderBy(e => e.FencingToken).ToListAsync());
        Assert.Contains(history, e => e.State == "Abandoned" && e.CheckpointAt == null);                             // K2's killed attempt
        Assert.Contains(history, e => e.State == "Abandoned" && e.CheckpointAt != null && e.FenceSource == "HumanConfirmed"); // K1's killed attempt
        Assert.Contains(history, e => e.Intent == "Reconcile" && e.Outcome == "ReconciledFound");
    }

    private static async Task<JsonElement> WaitForRecordAsync(HttpClient api, Guid job, string status, TimeSpan timeout, PlatformProcesses processes)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var record = (await JsonAsync(await api.GetAsync($"/api/v1/jobs/{job}/records"))).EnumerateArray().Single();
            if (record.GetProperty("status").GetString() == status)
            {
                return record;
            }

            Assert.True(record.GetProperty("status").GetString() is "Pending" or "Running" or "AwaitingFence", "unexpected record state: " + record);
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"Record never reached {status}: {record}{Environment.NewLine}" + string.Join(Environment.NewLine, processes.Processes.Select(p => p.Tail(30))));
            }

            await Task.Delay(1_000);
        }
    }
}
