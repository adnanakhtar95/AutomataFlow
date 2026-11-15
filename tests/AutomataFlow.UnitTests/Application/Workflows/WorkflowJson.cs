using System.Collections.Immutable;
using System.Text.Json.Nodes;
using AutomataFlow.Application.Validation;
using AutomataFlow.Application.Workflows.Definition;

namespace AutomataFlow.UnitTests.Application.Workflows;

/// <summary>Builders for test definitions (fictional data only).</summary>
internal static class WorkflowJson
{
    /// <summary>The smallest valid workflow: one terminal captureResult (explicit finalization, §16 D3).</summary>
    public const string Minimal =
        """{"schemaVersion":1,"name":"minimal-flow","allowedHosts":["https://crm.example.test"],"steps":[{"id":"done","type":"captureResult","config":{"outcome":{"status":"succeeded"}}}]}""";

    /// <summary>Every top-level section and every optional field, with fictional values.</summary>
    public const string Full =
        """
        {
          "schemaVersion": 1,
          "name": "democrm-register-customer",
          "description": "Registers one customer in a fictional CRM.",
          "timeoutSeconds": 90,
          "parameters": [ { "name": "crmBaseUrl", "type": "url" }, { "name": "crmUser", "type": "string" } ],
          "allowedHosts": [ "{{params.crmBaseUrl}}" ],
          "inputs": [
            { "name": "email", "type": "email", "required": true, "maxLength": 120 },
            { "name": "company", "type": "string" },
            { "name": "taxId", "type": "string", "required": false, "sensitive": true },
            { "name": "age", "type": "integer" }, { "name": "score", "type": "decimal" }, { "name": "vip", "type": "boolean" },
            { "name": "since", "type": "date" }, { "name": "contract", "type": "file" }
          ],
          "secrets": [ "crmPassword" ],
          "maskSelectors": [ { "css": "[data-testid=detail-tax-id]" }, { "role": "textbox", "name": "Tax id" } ],
          "reconciliation": {
            "evidence": "authoritative",
            "matchKey": "operationId",
            "authority": { "maxCommitLatencySeconds": 30, "contractReference": "Fictional CRM runbook 4.2: synchronous commit" }
          },
          "setup": [
            { "id": "login", "type": "navigate", "config": { "url": "{{params.crmBaseUrl}}/Login" }, "description": "Open the login page" },
            { "id": "op", "type": "fill", "config": { "selector": { "testId": "operation-id" }, "value": "{{operation.id}}" } }
          ],
          "reconcile": [
            { "id": "find", "type": "reconcileMatch", "config": {
                "rows": { "css": "tr.customer" }, "key": { "attribute": "data-operation-id" }, "equals": "{{operation.id}}",
                "emptyIndicator": { "testId": "no-results" }, "maxRows": 20 } }
          ],
          "steps": [
            { "id": "submit", "type": "click", "config": { "selector": { "testId": "save" } }, "timeoutMs": 15000, "sideEffect": true,
              "idempotency": { "key": "{{operation.id}}", "guarantee": "targetEnforced" } },
            { "id": "read-id", "type": "extract", "config": { "selector": { "testId": "customer-id" }, "source": "text" }, "onError": "continue" },
            { "id": "done", "type": "captureResult", "config": { "fields": { "customerId": "{{steps.read-id.value}}" }, "outcome": { "status": "succeeded" } } }
          ]
        }
        """;

    /// <summary>All 13 action types in valid positions (fictional CRM).</summary>
    public const string KitchenSink =
        """
        {
          "schemaVersion": 1,
          "name": "kitchen-sink",
          "parameters": [ { "name": "crmBaseUrl", "type": "url" }, { "name": "region", "type": "string" } ],
          "allowedHosts": [ "{{params.crmBaseUrl}}" ],
          "inputs": [ { "name": "email", "type": "email", "required": true }, { "name": "contract", "type": "file" } ],
          "secrets": [ "crmPassword" ],
          "reconciliation": { "evidence": "advisory", "matchKey": "businessKey" },
          "setup": [
            { "id": "open", "type": "navigate", "config": { "url": "{{params.crmBaseUrl}}/Login?region={{params.region}}", "acceptStatus": [404] } },
            { "id": "password", "type": "fill", "config": { "selector": { "label": "Password" }, "value": "{{secret.crmPassword}}" } },
            { "id": "sign-in", "type": "click", "config": { "selector": { "role": "button", "name": "Sign in" }, "waitForNavigation": true } },
            { "id": "loaded", "type": "wait", "config": { "for": "load" } }
          ],
          "reconcile": [
            { "id": "search", "type": "navigate", "config": { "url": "{{params.crmBaseUrl}}/Customers/Search?Term={{record.email}}" } },
            { "id": "match", "type": "reconcileMatch", "config": {
                "rows": { "css": "tr.customer" }, "key": { "text": { "css": "td.email" } }, "equals": "{{record.email}}", "ignoreCase": true,
                "emptyIndicator": { "testId": "no-results" }, "fields": { "customerId": { "attribute": "data-id" } } } }
          ],
          "steps": [
            { "id": "op", "type": "fill", "config": { "selector": { "testId": "operation-id" }, "value": "{{operation.id}}" } },
            { "id": "plan", "type": "select", "config": { "selector": { "testId": "plan" }, "label": "Gold" }, "onError": "continue" },
            { "id": "attach", "type": "upload", "config": { "selector": { "testId": "file" }, "file": "{{record.contract}}", "allowedExtensions": [".pdf"] },
              "sideEffect": true, "timeoutMs": 30000 },
            { "id": "pause", "type": "delay", "config": { "milliseconds": 250 } },
            { "id": "save", "type": "click", "config": { "selector": { "testId": "save" }, "waitForNavigation": true }, "sideEffect": true, "timeoutMs": 20000 },
            { "id": "confirmed", "type": "wait", "config": { "url": "**/Customers/Confirmation/**" }, "onError": "continue" },
            { "id": "read-id", "type": "extract", "config": { "selector": { "testId": "customer-id" }, "source": "attribute", "attribute": "data-id", "maxLength": 50 } },
            { "id": "proof", "type": "screenshot", "config": { "label": "confirmation", "fullPage": true } },
            { "id": "receipt", "type": "download", "config": { "trigger": { "testId": "receipt" } } },
            { "id": "check", "type": "condition", "config": {
                "if": { "any": [ { "urlMatches": "**/Customers/Confirmation/**" }, { "not": { "elementVisible": { "testId": "form-error" } } } ] },
                "then": [ { "id": "ok", "type": "captureResult", "config": { "fields": { "customerId": "{{steps.read-id.value}}" }, "outcome": { "status": "succeeded" } } } ],
                "else": [ { "id": "dup", "type": "captureResult", "config": { "outcome": { "status": "failed", "code": "DuplicateCustomer" } } } ] } }
          ]
        }
        """;

    public static string With(Action<JsonObject> mutate) => With(Minimal, mutate);

    public static string With(string baseJson, Action<JsonObject> mutate)
    {
        var root = JsonNode.Parse(baseJson)!.AsObject();
        mutate(root);
        return root.ToJsonString();
    }

    /// <summary>A step with a valid sample config for its type (a delay by default).</summary>
    public static JsonObject Step(string id, string type = "delay") => new() { ["id"] = id, ["type"] = type, ["config"] = SampleConfig(type) };

    /// <summary>A terminal captureResult step.</summary>
    public static JsonObject Terminal(string id = "done", string status = "succeeded") =>
        new() { ["id"] = id, ["type"] = "captureResult", ["config"] = new JsonObject { ["outcome"] = new JsonObject { ["status"] = status } } };

    /// <summary><paramref name="count"/> delay steps.</summary>
    public static JsonArray Delays(int count, string prefix) => [.. Enumerable.Range(0, count).Select(i => (JsonNode)Step(prefix + i))];

    /// <summary><paramref name="count"/> delay steps followed by a terminal captureResult (count + 1 steps).</summary>
    public static JsonArray StepsThenDone(int count, string prefix) => [.. Enumerable.Range(0, count).Select(i => (JsonNode)Step(prefix + i)), Terminal()];

    public static JsonObject SampleConfig(string type) => type switch
    {
        "delay" => new JsonObject { ["milliseconds"] = 1 },
        "navigate" => new JsonObject { ["url"] = "https://crm.example.test/Customers" },
        "click" => new JsonObject { ["selector"] = new JsonObject { ["testId"] = "go" } },
        "fill" => new JsonObject { ["selector"] = new JsonObject { ["testId"] = "f" }, ["value"] = "x" },
        "select" => new JsonObject { ["selector"] = new JsonObject { ["testId"] = "s" }, ["index"] = 0 },
        "wait" => new JsonObject { ["for"] = "load" },
        "extract" => new JsonObject { ["selector"] = new JsonObject { ["testId"] = "e" }, ["source"] = "text" },
        "screenshot" => new JsonObject { ["label"] = "shot" },
        "download" => new JsonObject { ["trigger"] = new JsonObject { ["testId"] = "d" } },
        "captureResult" => new JsonObject { ["outcome"] = new JsonObject { ["status"] = "succeeded" } },
        _ => new JsonObject(),
    };

    public static JsonObject FirstStep(JsonObject root) => root["steps"]!.AsArray()[0]!.AsObject();

    public static ImmutableArray<ValidationError> Errors(string json) => ValidatedWorkflow.CreateForPublication(json).Errors;

    public static ValidatedWorkflow Valid(string json)
    {
        var result = ValidatedWorkflow.CreateForPublication(json);
        Assert.True(result.IsValid, "Expected a valid definition, got: " + string.Join("; ", result.Errors.Select(e => $"{e.Path} {e.Code} {e.Message}")));
        return result.Workflow!;
    }

    /// <summary>Asserts the definition is refused, no workflow is produced, and the given error is among the errors.</summary>
    public static void AssertRejected(string json, string path, string code)
    {
        var result = ValidatedWorkflow.CreateForPublication(json);
        Assert.False(result.IsValid);
        Assert.Null(result.Workflow);
        Assert.True(
            result.Errors.Any(e => e.Path == path && e.Code == code),
            $"Expected {path} {code}; got: " + string.Join("; ", result.Errors.Select(e => $"{e.Path} {e.Code}")));
    }

    public static string Repeat(char c, int count) => new(c, count);
}
