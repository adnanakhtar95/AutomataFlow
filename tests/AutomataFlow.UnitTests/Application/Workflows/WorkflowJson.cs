using System.Collections.Immutable;
using System.Text.Json.Nodes;
using AutomataFlow.Application.Validation;
using AutomataFlow.Application.Workflows.Definition;

namespace AutomataFlow.UnitTests.Application.Workflows;

/// <summary>Builders for test definitions (fictional data only).</summary>
internal static class WorkflowJson
{
    public const string Minimal =
        """{"schemaVersion":1,"name":"minimal-flow","allowedHosts":["https://crm.example.test"],"steps":[{"id":"done","type":"captureResult","config":{}}]}""";

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
          "setup": [ { "id": "login", "type": "navigate", "config": { "url": "{{params.crmBaseUrl}}/Login" }, "description": "Open the login page" } ],
          "reconcile": [ { "id": "find", "type": "reconcileMatch", "config": { "maxRows": 20 } } ],
          "steps": [
            { "id": "submit", "type": "click", "config": { "selector": { "testId": "save" } }, "timeoutMs": 15000, "sideEffect": true,
              "idempotency": { "key": "{{operation.id}}", "guarantee": "targetEnforced" } },
            { "id": "read-id", "type": "extract", "config": {}, "onError": "continue" },
            { "id": "done", "type": "captureResult", "config": { "outcome": { "status": "succeeded" } } }
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

    public static JsonObject Step(string id, string type = "delay") => new() { ["id"] = id, ["type"] = type, ["config"] = new JsonObject() };

    public static JsonArray Steps(int count, string prefix) => [.. Enumerable.Range(0, count).Select(i => (JsonNode)Step(prefix + i))];

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
