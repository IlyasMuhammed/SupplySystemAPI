using FluentAssertions;
using Moq;
using SMS.Shared.Exceptions;
using SMS.WorkflowEngine.Data;
using SMS.WorkflowEngine.Models;
using SMS.WorkflowEngine.Services;
using SMS.WorkflowEngine.Tests.Helpers;
using Xunit;

namespace SMS.WorkflowEngine.Tests;

public class WorkflowResolutionService_NoWorkflow
{
    [Fact]
    public async Task Throws_WorkflowNotFoundException_when_no_active_definition_exists()
    {
        var db      = DbFactory.Create();
        var svc     = new WorkflowResolutionService(db, new Mock<IApproverResolutionService>().Object);
        var command = new SubmitDocumentCommand { InterfaceCode = "PO", DocumentId = Guid.NewGuid(), SubmittedBy = 1 };

        var act = () => svc.ResolveWorkflowAsync(command);

        await act.Should().ThrowAsync<WorkflowNotFoundException>()
            .WithMessage("*PO*");
    }

    [Fact]
    public async Task Throws_WorkflowNotFoundException_when_definition_exists_but_is_not_active()
    {
        var db = DbFactory.Create(db =>
        {
            var def = Fake.Definition();
            def.IsActive = false;
            db.WorkflowDefinitions.Add(def);
        });

        var svc     = new WorkflowResolutionService(db, new Mock<IApproverResolutionService>().Object);
        var command = new SubmitDocumentCommand { InterfaceCode = "PO", DocumentId = Guid.NewGuid(), SubmittedBy = 1 };

        var act = () => svc.ResolveWorkflowAsync(command);

        await act.Should().ThrowAsync<WorkflowNotFoundException>();
    }

    [Fact]
    public async Task Throws_WorkflowNotFoundException_when_only_conditional_definition_exists_but_condition_value_is_null()
    {
        var db = DbFactory.Create(db =>
        {
            var def = Fake.Definition(conditionOperator: "GT", conditionValue: 1000m);
            db.WorkflowDefinitions.Add(def);
        });

        var svc     = new WorkflowResolutionService(db, new Mock<IApproverResolutionService>().Object);
        var command = new SubmitDocumentCommand { InterfaceCode = "PO", DocumentId = Guid.NewGuid(), SubmittedBy = 1, ConditionValue = null };

        var act = () => svc.ResolveWorkflowAsync(command);

        await act.Should().ThrowAsync<WorkflowNotFoundException>();
    }
}

public class WorkflowResolutionService_ConditionalRouting
{
    private static (WorkflowResolutionService svc, Mock<IApproverResolutionService> approvers)
        BuildSvc(Action<WorkflowDbContext> seed)
    {
        var db        = DbFactory.Create(seed);
        var approvers = new Mock<IApproverResolutionService>();
        approvers.Setup(a => a.ResolveApproversAsync(It.IsAny<Domain.WorkflowStep>(), It.IsAny<int>()))
                 .ReturnsAsync((IReadOnlyList<ResolvedApprover>)[new ResolvedApprover(Fake.User1, "Alice")]);
        return (new WorkflowResolutionService(db, approvers.Object), approvers);
    }

    [Theory]
    [InlineData("GT",  1001, true)]   // 1001 > 1000  → conditional selected
    [InlineData("GT",  1000, false)]  // 1000 == 1000 → not matched, fallback to unconditional
    [InlineData("GT",   999, false)]
    [InlineData("GTE", 1000, true)]   // 1000 >= 1000 → selected
    [InlineData("GTE", 1001, true)]
    [InlineData("GTE",  999, false)]
    [InlineData("LT",   999, true)]   // 999 < 1000   → selected
    [InlineData("LT",  1000, false)]
    [InlineData("LTE", 1000, true)]   // 1000 <= 1000 → selected
    [InlineData("LTE",  999, true)]
    [InlineData("LTE", 1001, false)]
    [InlineData("EQ",  1000, true)]   // exact match
    [InlineData("EQ",  1001, false)]
    public async Task Selects_correct_workflow_based_on_operator(
        string op, decimal conditionValue, bool expectConditional)
    {
        var conditionalName   = "Senior Manager Approval";
        var unconditionalName = "Standard Approval";

        var (svc, _) = BuildSvc(db =>
        {
            db.WorkflowDefinitions.AddRange(
                Fake.Definition(conditionOperator: op, conditionValue: 1000m).With(d => d.Name = conditionalName, d => d.Id = 1),
                Fake.Definition().With(d => d.Name = unconditionalName, d => d.Id = 2));

            var step = Fake.Step(definitionId: 1);
            db.WorkflowSteps.Add(step);
            var step2 = Fake.Step(definitionId: 2);
            db.WorkflowSteps.Add(step2);
        });

        var result = await svc.ResolveWorkflowAsync(new SubmitDocumentCommand
        {
            InterfaceCode  = "PO",
            DocumentId     = Guid.NewGuid(),
            SubmittedBy    = 1,
            ConditionValue = conditionValue
        });

        var expected = expectConditional ? conditionalName : unconditionalName;
        result.Workflow.Name.Should().Be(expected);
    }

    [Fact]
    public async Task BETWEEN_selects_workflow_when_value_is_within_range()
    {
        var (svc, _) = BuildSvc(db =>
        {
            db.WorkflowDefinitions.Add(Fake.Definition(
                conditionOperator: "BETWEEN", conditionValueMin: 100m, conditionValueMax: 500m)
                .With(d => d.Name = "Mid-Range Approval", d => d.Id = 1));
            db.WorkflowSteps.Add(Fake.Step(definitionId: 1));
        });

        var result = await svc.ResolveWorkflowAsync(new SubmitDocumentCommand
        {
            InterfaceCode  = "PO",
            DocumentId     = Guid.NewGuid(),
            SubmittedBy    = 1,
            ConditionValue = 300m
        });

        result.Workflow.Name.Should().Be("Mid-Range Approval");
    }

    [Theory]
    [InlineData(99)]
    [InlineData(501)]
    public async Task BETWEEN_falls_back_to_unconditional_when_value_is_outside_range(decimal value)
    {
        var (svc, _) = BuildSvc(db =>
        {
            db.WorkflowDefinitions.AddRange(
                Fake.Definition(conditionOperator: "BETWEEN", conditionValueMin: 100m, conditionValueMax: 500m)
                    .With(d => d.Name = "Mid-Range", d => d.Id = 1),
                Fake.Definition().With(d => d.Name = "Fallback", d => d.Id = 2));
            db.WorkflowSteps.AddRange(Fake.Step(1), Fake.Step(2));
        });

        var result = await svc.ResolveWorkflowAsync(new SubmitDocumentCommand
        {
            InterfaceCode = "PO", DocumentId = Guid.NewGuid(), SubmittedBy = 1,
            ConditionValue = value
        });

        result.Workflow.Name.Should().Be("Fallback");
    }

    [Fact]
    public async Task Conditional_definition_takes_priority_over_unconditional_when_both_match()
    {
        var (svc, _) = BuildSvc(db =>
        {
            db.WorkflowDefinitions.AddRange(
                Fake.Definition(conditionOperator: "GT", conditionValue: 0m)
                    .With(d => d.Name = "Conditional", d => d.Id = 1),
                Fake.Definition().With(d => d.Name = "Unconditional", d => d.Id = 2));
            db.WorkflowSteps.AddRange(Fake.Step(1), Fake.Step(2));
        });

        var result = await svc.ResolveWorkflowAsync(new SubmitDocumentCommand
        {
            InterfaceCode = "PO", DocumentId = Guid.NewGuid(), SubmittedBy = 1,
            ConditionValue = 1m
        });

        result.Workflow.Name.Should().Be("Conditional");
    }
}

public class WorkflowResolutionService_SkipCondition
{
    private static WorkflowResolutionService BuildSvc(Action<WorkflowDbContext> seed)
    {
        var db        = DbFactory.Create(seed);
        var approvers = new Mock<IApproverResolutionService>();
        approvers.Setup(a => a.ResolveApproversAsync(It.IsAny<Domain.WorkflowStep>(), It.IsAny<int>()))
                 .ReturnsAsync((IReadOnlyList<ResolvedApprover>)[new ResolvedApprover(Fake.User1, "Alice")]);
        return new WorkflowResolutionService(db, approvers.Object);
    }

    [Fact]
    public async Task Step_with_skip_condition_true_is_marked_skipped()
    {
        var svc = BuildSvc(db =>
        {
            var def = Fake.Definition();
            db.WorkflowDefinitions.Add(def);
            // skip when value < 10000 (value will be 5000 → skip = true)
            db.WorkflowSteps.Add(Fake.Step(definitionId: def.Id, skipCondition: "ConditionValue < 10000"));
        });

        var result = await svc.ResolveWorkflowAsync(new SubmitDocumentCommand
        {
            InterfaceCode = "PO", DocumentId = Guid.NewGuid(), SubmittedBy = 1,
            ConditionValue = 5000m
        });

        result.Steps.Should().ContainSingle(s => s.IsSkipped);
    }

    [Fact]
    public async Task Step_with_skip_condition_false_is_not_skipped()
    {
        var svc = BuildSvc(db =>
        {
            var def = Fake.Definition();
            db.WorkflowDefinitions.Add(def);
            db.WorkflowSteps.Add(Fake.Step(definitionId: def.Id, skipCondition: "ConditionValue < 10000"));
        });

        var result = await svc.ResolveWorkflowAsync(new SubmitDocumentCommand
        {
            InterfaceCode = "PO", DocumentId = Guid.NewGuid(), SubmittedBy = 1,
            ConditionValue = 15000m
        });

        result.Steps.Single().IsSkipped.Should().BeFalse();
    }

    [Fact]
    public async Task Null_skip_condition_never_skips_step()
    {
        var svc = BuildSvc(db =>
        {
            var def = Fake.Definition();
            db.WorkflowDefinitions.Add(def);
            db.WorkflowSteps.Add(Fake.Step(definitionId: def.Id, skipCondition: null));
        });

        var result = await svc.ResolveWorkflowAsync(new SubmitDocumentCommand
        {
            InterfaceCode = "PO", DocumentId = Guid.NewGuid(), SubmittedBy = 1
        });

        result.Steps.Single().IsSkipped.Should().BeFalse();
    }

    [Fact]
    public async Task Malformed_skip_condition_fails_safe_and_does_not_skip()
    {
        var svc = BuildSvc(db =>
        {
            var def = Fake.Definition();
            db.WorkflowDefinitions.Add(def);
            db.WorkflowSteps.Add(Fake.Step(definitionId: def.Id, skipCondition: "NOT_VALID_EXPRESSION!!!"));
        });

        var result = await svc.ResolveWorkflowAsync(new SubmitDocumentCommand
        {
            InterfaceCode = "PO", DocumentId = Guid.NewGuid(), SubmittedBy = 1,
            ConditionValue = 9999m
        });

        result.Steps.Single().IsSkipped.Should().BeFalse();
    }

    [Fact]
    public async Task Skipped_step_does_not_call_approver_resolution()
    {
        var db        = DbFactory.Create(db =>
        {
            var def = Fake.Definition();
            db.WorkflowDefinitions.Add(def);
            db.WorkflowSteps.Add(Fake.Step(definitionId: def.Id, skipCondition: "ConditionValue < 10000"));
        });
        var approvers = new Mock<IApproverResolutionService>();
        var svc       = new WorkflowResolutionService(db, approvers.Object);

        await svc.ResolveWorkflowAsync(new SubmitDocumentCommand
        {
            InterfaceCode = "PO", DocumentId = Guid.NewGuid(), SubmittedBy = 1,
            ConditionValue = 5000m  // triggers skip
        });

        approvers.Verify(
            a => a.ResolveApproversAsync(It.IsAny<Domain.WorkflowStep>(), It.IsAny<int>()),
            Times.Never);
    }

    [Fact]
    public async Task Resolution_snapshot_captures_AllowRecall_and_AllowReissue_from_definition()
    {
        var svc = BuildSvc(db =>
        {
            var def = Fake.Definition();
            def.AllowRecall  = false;
            def.AllowReissue = false;
            db.WorkflowDefinitions.Add(def);
            db.WorkflowSteps.Add(Fake.Step(definitionId: def.Id));
        });

        var result = await svc.ResolveWorkflowAsync(new SubmitDocumentCommand
        {
            InterfaceCode = "PO", DocumentId = Guid.NewGuid(), SubmittedBy = 1
        });

        result.Workflow.AllowRecall.Should().BeFalse();
        result.Workflow.AllowReissue.Should().BeFalse();
    }
}

// ── Fluent helper for seeding entity mutations ────────────────────────────────

file static class EntityExtensions
{
    internal static T With<T>(this T entity, params Action<T>[] mutations)
    {
        foreach (var m in mutations) m(entity);
        return entity;
    }
}
