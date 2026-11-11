using DemoCrm.Configuration;
using DemoCrm.Customers;
using DemoCrm.Simulation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace DemoCrm.Pages.Customers;

public sealed class RegisterModel(
    ICustomerStore store,
    FailureSimulator simulator,
    IOptions<DemoCrmOptions> options,
    TimeProvider time,
    ILogger<RegisterModel> logger) : PageModel
{
    public const string IdempotencyKeyHeader = "Idempotency-Key";

    [BindProperty]
    public CustomerInput Input { get; set; } = new();

    /// <summary>Machine-readable banner kind: validation, duplicate, operation-conflict or temporary.</summary>
    public string? BannerKind { get; private set; }

    public string? BannerMessage { get; private set; }

    public string? ExistingCustomerId { get; private set; }

    public void OnGet()
    {
        // A fresh operation reference per form instance makes accidental double submits idempotent.
        Input.OperationId = Guid.NewGuid().ToString("D");
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        ModelState.Clear();
        IReadOnlyList<ValidationFailure> failures = CustomerInputValidator.Validate(Input, out var customer);
        string? operationId = null;

        if (customer is not null)
        {
            // The form field wins; the Idempotency-Key header serves clients that do not use the form field.
            var supplied = string.IsNullOrWhiteSpace(Input.OperationId)
                ? Request.Headers[IdempotencyKeyHeader].ToString()
                : Input.OperationId;
            if (!string.IsNullOrWhiteSpace(supplied))
            {
                if (Guid.TryParse(supplied.Trim(), out var parsed))
                {
                    operationId = parsed.ToString("D");
                }
                else
                {
                    failures = [new ValidationFailure(nameof(CustomerInput.OperationId), "Operation reference must be a valid GUID.")];
                    customer = null;
                }
            }
        }

        if (customer is null)
        {
            foreach (var failure in failures)
            {
                ModelState.AddModelError($"{nameof(Input)}.{failure.Field}", failure.Message);
            }

            return Fail(StatusCodes.Status422UnprocessableEntity, "validation", "Please correct the highlighted fields.");
        }

        var scenario = options.Value.Simulation.Enabled ? FailureScenario.Parse(customer.Email) : FailureScenario.None;
        var attempt = scenario.Kind == FailureScenarioKind.None ? 0 : simulator.RecordAttempt(customer.Email);

        var failCount = scenario.FailCount;
        if (scenario.Kind == FailureScenarioKind.FailBeforeCommit && attempt <= failCount)
        {
            logger.LogInformation("Simulated temporary failure before commit (attempt {Attempt} of {FailCount})", attempt, failCount);
            return TemporaryFailure();
        }

        if (scenario.Kind == FailureScenarioKind.Slow)
        {
            await Task.Delay(options.Value.Simulation.SlowDelayMs, cancellationToken);
        }

        var outcome = store.Register(customer, operationId, time.GetUtcNow());
        var customerId = outcome.Customer.DisplayId;

        if (scenario.Kind == FailureScenarioKind.CommitThenFail && attempt == 1 && outcome.Status == RegistrationStatus.Created)
        {
            logger.LogInformation("Simulated temporary failure AFTER commit of {CustomerId}", customerId);
            return TemporaryFailure();
        }

        switch (outcome.Status)
        {
            case RegistrationStatus.Created:
                logger.LogInformation("Registered customer {CustomerId}", customerId);
                return RedirectToPage("Confirmation", new { id = outcome.Customer.Id, result = "created" });

            case RegistrationStatus.Replayed:
                logger.LogInformation("Replayed registration for {CustomerId}", customerId);
                return RedirectToPage("Confirmation", new { id = outcome.Customer.Id, result = "replayed" });

            case RegistrationStatus.DuplicateEmail:
                ModelState.AddModelError($"{nameof(Input)}.{nameof(CustomerInput.Email)}", $"A customer with this email already exists ({customerId}).");
                ExistingCustomerId = customerId;
                return Fail(StatusCodes.Status409Conflict, "duplicate", $"Duplicate customer: this email is already registered as {customerId}.");

            case RegistrationStatus.DuplicatePhone:
                ModelState.AddModelError($"{nameof(Input)}.{nameof(CustomerInput.Phone)}", $"A customer with this phone number already exists ({customerId}).");
                ExistingCustomerId = customerId;
                return Fail(StatusCodes.Status409Conflict, "duplicate", $"Duplicate customer: this phone number is already registered as {customerId}.");

            default:
                ModelState.AddModelError($"{nameof(Input)}.{nameof(CustomerInput.OperationId)}", "This operation reference was already used with different data.");
                return Fail(StatusCodes.Status409Conflict, "operation-conflict", "Operation reference conflict: it was already used with different customer data.");
        }
    }

    private PageResult TemporaryFailure()
    {
        Response.Headers.RetryAfter = "1";
        return Fail(StatusCodes.Status503ServiceUnavailable, "temporary", "Temporary service error. Please try again.");
    }

    private PageResult Fail(int statusCode, string kind, string message)
    {
        BannerKind = kind;
        BannerMessage = message;
        var result = (PageResult)Page();
        result.StatusCode = statusCode;
        return result;
    }
}
