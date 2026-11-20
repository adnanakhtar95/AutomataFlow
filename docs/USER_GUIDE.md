# AutomataFlow — User guide

For the people who use AutomataFlow every day: workspace owners, administrators, operators and viewers. Operators of
the platform itself (installation, backups, fences) use docs/DEPLOYMENT.md and docs/RUNBOOK.md.

## 1. Getting access
- Your workspace is created by your provider (a platform operator). The workspace Owner receives a one-time invitation
  link, opens it, chooses a password (at least 12 characters) and lands in the workspace.
- Owners and Admins invite colleagues under **Members**: enter the e-mail address and a role, then send the link that is
  shown once (AutomataFlow sends no e-mail). Links expire after 7 days and work once. If someone already has an account,
  they sign in with their existing password to accept.
- If you belong to several workspaces, switch between them in the user menu (top right).

| Role | Can |
|---|---|
| Viewer | see workflows, jobs, records, logs, screenshots; export results |
| Operator | everything a Viewer can, plus create and edit workflow drafts, create and cancel jobs |
| Admin | everything an Operator can, plus publish workflows, requeue records that need review, manage secrets and members (except Owners) |
| Owner | everything, including Owner roles and renaming the workspace |

## 2. Setting up a workflow
1. **Workflows → New workflow.** Enter a name, then either pick a **template** (CRM customer registration, e-commerce
   order entry, invoice approval) or build one with the guided form or the JSON editor.
2. **Validate** shows every problem with its location (for example `steps[3].config.selector`). Fix and save the draft.
3. **Secrets** (Admins): store the passwords the workflow uses (for example `crmPassword`). Values are encrypted and can
   never be read back; they never appear in logs, results or screenshots.
4. **Publish** (Admins). If the workflow claims that the target system refuses duplicate submissions
   (`targetEnforced`), you must confirm that claim: if it is false, a retried record could create a duplicate. Published
   versions never change; edits create a new version.

Adapting a template to your own system means replacing selectors, URLs and fields; docs/WORKFLOWS.md explains every
action and packages/README.md explains the packages.

## 3. Running a job
1. **New job:** choose a published workflow, fill in its parameters (for example the address of your CRM), and upload a
   `.csv` (with a header row) or `.json` file. "Ignore columns the workflow does not declare" lets you keep extra columns.
2. The summary shows how many records were imported and how many were **invalid** (they are never executed; their
   validation errors are listed on the job page).
3. The job page refreshes on its own while records run. **Cancel job** stops records that have not started; records
   already running finish safely first.

## 4. Understanding record outcomes
| Status | Meaning | What to do |
|---|---|---|
| Succeeded | the workflow completed; its result fields are in the record and the export | nothing |
| Reconciled | an earlier attempt's outcome was uncertain; the platform found it in the target system and did not repeat it | nothing |
| Failed | the workflow declared a business failure (e.g. duplicate customer, amount mismatch) or a permanent technical error | read the error code and the attempt's screenshot and logs; fix the data and run a new job |
| Invalid | the record failed validation at import | fix the file |
| Needs review | the platform could not prove whether the target system was changed, so it will not retry on its own | check the target system; an Admin can **Requeue for reconciliation** (it looks up the target first and never repeats blindly) |
| Awaiting fence | a Worker stopped mid-record; the platform is proving it is gone | wait; it resolves on its own or becomes Needs review |
| Cancelled | the job was cancelled before this record started | nothing |

Click a record to see each attempt: steps with durations and error codes, logs, screenshots and downloads. Screenshots
are shown only to members of your workspace.

## 5. Results
**Export results** downloads a CSV with row number, operation id, status, error code and the result fields. It never
contains the input values you uploaded. Cells that a spreadsheet could interpret as formulas are neutralized.

## 6. Good practice
- Run a small file first (a few rows) after changing a workflow.
- Keep record files free of data the workflow does not need; mark personal inputs as sensitive in the workflow.
- Remove members who leave (Members → Remove); their sessions end immediately.
