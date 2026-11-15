using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AutomataFlow.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Database-enforced journal invariants (ADR-071): "Blocking" is always derived from the row (so the partial unique index
    /// UX_AttemptJournal_OneBlocking really means "one Reserved, Started or unfenced attempt per operation"), and history is
    /// append-only: only never-started Reserved rows can be deleted, identity is immutable, states only move forward, and a
    /// recorded fact (start, checkpoint, abandonment, finish, fence, outcome, observation) can never change once set.
    /// </summary>
    public partial class AttemptJournalTriggers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE FUNCTION af_journal_blocking() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  NEW."Blocking" := COALESCE(NEW."State" IN ('Reserved', 'Started'), false)
                    OR (COALESCE(NEW."State" IN ('Completed', 'Abandoned'), false)
                        AND (NEW."CheckpointAt" IS NOT NULL OR COALESCE(NEW."CompletionSideEffect" IN ('Started', 'Unknown'), false))
                        AND NOT COALESCE(NEW."ExecutionFenced", false) AND NEW."FencedAt" IS NULL);
                  RETURN NEW;
                END $$;

                CREATE TRIGGER trg_attempt_journal_blocking BEFORE INSERT OR UPDATE ON "AttemptJournal"
                  FOR EACH ROW EXECUTE FUNCTION af_journal_blocking();

                CREATE FUNCTION af_journal_guard() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  IF TG_OP = 'DELETE' THEN
                    IF OLD."State" <> 'Reserved' THEN
                      RAISE EXCEPTION 'attempt journal history is append-only' USING ERRCODE = 'AF001';
                    END IF;
                    RETURN OLD;
                  END IF;
                  IF NEW."OrganizationId" <> OLD."OrganizationId" OR NEW."OperationId" <> OLD."OperationId" OR NEW."AttemptNumber" <> OLD."AttemptNumber"
                     OR NEW."IncarnationId" <> OLD."IncarnationId" OR NEW."FencingToken" <> OLD."FencingToken" OR NEW."Intent" <> OLD."Intent" THEN
                    RAISE EXCEPTION 'attempt journal identity is immutable' USING ERRCODE = 'AF001';
                  END IF;
                  IF NOT (OLD."State" = NEW."State"
                          OR (OLD."State" = 'Reserved' AND NEW."State" = 'Started')
                          OR (OLD."State" = 'Started' AND NEW."State" IN ('Completed', 'Abandoned'))) THEN
                    RAISE EXCEPTION 'invalid attempt journal state transition' USING ERRCODE = 'AF001';
                  END IF;
                  IF (OLD."StartedAt" IS NOT NULL AND NEW."StartedAt" IS DISTINCT FROM OLD."StartedAt")
                     OR (OLD."CheckpointAt" IS NOT NULL AND NEW."CheckpointAt" IS DISTINCT FROM OLD."CheckpointAt")
                     OR (OLD."ObservedAt" IS NOT NULL AND NEW."ObservedAt" IS DISTINCT FROM OLD."ObservedAt")
                     OR (OLD."AbandonedAt" IS NOT NULL AND NEW."AbandonedAt" IS DISTINCT FROM OLD."AbandonedAt")
                     OR (OLD."FinishedAt" IS NOT NULL AND NEW."FinishedAt" IS DISTINCT FROM OLD."FinishedAt")
                     OR (OLD."Outcome" IS NOT NULL AND NEW."Outcome" IS DISTINCT FROM OLD."Outcome")
                     OR (OLD."FencedAt" IS NOT NULL AND NEW."FencedAt" IS DISTINCT FROM OLD."FencedAt")
                     OR (OLD."ExecutionFenced" AND NOT NEW."ExecutionFenced") THEN
                    RAISE EXCEPTION 'recorded attempt journal facts are immutable' USING ERRCODE = 'AF001';
                  END IF;
                  RETURN NEW;
                END $$;

                CREATE TRIGGER trg_attempt_journal_guard BEFORE UPDATE OR DELETE ON "AttemptJournal"
                  FOR EACH ROW EXECUTE FUNCTION af_journal_guard();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_attempt_journal_guard ON "AttemptJournal";
                DROP FUNCTION IF EXISTS af_journal_guard();
                DROP TRIGGER IF EXISTS trg_attempt_journal_blocking ON "AttemptJournal";
                DROP FUNCTION IF EXISTS af_journal_blocking();
                """);
        }
    }
}
