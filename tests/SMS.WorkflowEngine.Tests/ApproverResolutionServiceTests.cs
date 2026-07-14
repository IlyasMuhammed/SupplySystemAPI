using FluentAssertions;
using Moq;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.WorkflowEngine.Data;
using SMS.WorkflowEngine.Domain;
using SMS.WorkflowEngine.Services;
using SMS.WorkflowEngine.Tests.Helpers;
using Xunit;

namespace SMS.WorkflowEngine.Tests;

public class ApproverResolutionService_UserType
{
    private static (ApproverResolutionService svc, Mock<IUserQueryService> userQuery) Build()
    {
        var uq  = new Mock<IUserQueryService>();
        var org = new Mock<IOrgChartService>();
        var svc = new ApproverResolutionService(DbFactory.Create(), uq.Object, org.Object);
        return (svc, uq);
    }

    private static WorkflowStep Step(string approverType = "USER", int? approverRefId = Fake.User1)
        => Fake.Step(approverType: approverType, approverRefId: approverRefId);

    [Fact]
    public async Task USER_type_returns_single_active_user()
    {
        var (svc, uq) = Build();
        uq.Setup(u => u.GetUserAsync(Fake.User1)).ReturnsAsync(new UserIdentity(Fake.User1, "Alice"));

        var result = await svc.ResolveApproversAsync(Step("USER", Fake.User1), submittedBy: 1);

        result.Should().ContainSingle(a => a.UserId == Fake.User1 && a.DisplayName == "Alice");
    }

    [Fact]
    public async Task USER_type_throws_WorkflowConfigurationException_when_user_not_found()
    {
        var (svc, uq) = Build();
        uq.Setup(u => u.GetUserAsync(It.IsAny<int>())).ReturnsAsync((UserIdentity?)null);

        var act = () => svc.ResolveApproversAsync(Step("USER", Fake.User1), submittedBy: 1);

        await act.Should().ThrowAsync<WorkflowConfigurationException>().WithMessage("*inactive*");
    }

    [Fact]
    public async Task USER_type_throws_WorkflowConfigurationException_when_ApproverRefId_is_null()
    {
        var (svc, _) = Build();

        var act = () => svc.ResolveApproversAsync(Step("USER", approverRefId: null), submittedBy: 1);

        await act.Should().ThrowAsync<WorkflowConfigurationException>().WithMessage("*ApproverRefId*");
    }
}

public class ApproverResolutionService_RoleType
{
    private static (ApproverResolutionService svc, Mock<IUserQueryService> userQuery) Build()
    {
        var uq  = new Mock<IUserQueryService>();
        var org = new Mock<IOrgChartService>();
        return (new ApproverResolutionService(DbFactory.Create(), uq.Object, org.Object), uq);
    }

    [Fact]
    public async Task ROLE_type_returns_all_active_users_in_role()
    {
        var (svc, uq) = Build();
        uq.Setup(u => u.GetActiveUsersByRoleAsync(5))
          .ReturnsAsync((IReadOnlyList<UserIdentity>)[
              new UserIdentity(Fake.User1, "Alice"),
              new UserIdentity(Fake.User2, "Bob")]);

        var result = await svc.ResolveApproversAsync(
            Fake.Step(approverType: "ROLE", approverRefId: 5), submittedBy: 1);

        result.Should().HaveCount(2);
        result.Should().Contain(a => a.UserId == Fake.User1);
        result.Should().Contain(a => a.UserId == Fake.User2);
    }

    [Fact]
    public async Task ROLE_type_throws_ApproverResolutionException_when_role_has_no_active_users()
    {
        var (svc, uq) = Build();
        uq.Setup(u => u.GetActiveUsersByRoleAsync(It.IsAny<int>()))
          .ReturnsAsync((IReadOnlyList<UserIdentity>)[]);

        var act = () => svc.ResolveApproversAsync(
            Fake.Step(approverType: "ROLE", approverRefId: 5), submittedBy: 1);

        await act.Should().ThrowAsync<ApproverResolutionException>().WithMessage("*no active users*");
    }

    [Fact]
    public async Task ROLE_type_throws_WorkflowConfigurationException_when_ApproverRefId_is_null()
    {
        var (svc, _) = Build();

        var act = () => svc.ResolveApproversAsync(
            Fake.Step(approverType: "ROLE", approverRefId: null), submittedBy: 1);

        await act.Should().ThrowAsync<WorkflowConfigurationException>().WithMessage("*ApproverRefId*");
    }
}

public class ApproverResolutionService_DepartmentType
{
    private static (ApproverResolutionService svc, Mock<IOrgChartService> orgChart) Build()
    {
        var uq  = new Mock<IUserQueryService>();
        var org = new Mock<IOrgChartService>();
        return (new ApproverResolutionService(DbFactory.Create(), uq.Object, org.Object), org);
    }

    [Fact]
    public async Task DEPARTMENT_type_returns_department_head()
    {
        var (svc, org) = Build();
        org.Setup(o => o.GetDepartmentHeadAsync(10)).ReturnsAsync(new UserIdentity(Fake.User1, "Dept Head"));

        var result = await svc.ResolveApproversAsync(
            Fake.Step(approverType: "DEPARTMENT", approverRefId: 10), submittedBy: 1);

        result.Should().ContainSingle(a => a.UserId == Fake.User1);
    }

    [Fact]
    public async Task DEPARTMENT_type_throws_ApproverResolutionException_when_no_head_assigned()
    {
        var (svc, org) = Build();
        org.Setup(o => o.GetDepartmentHeadAsync(It.IsAny<int>())).ReturnsAsync((UserIdentity?)null);

        var act = () => svc.ResolveApproversAsync(
            Fake.Step(approverType: "DEPARTMENT", approverRefId: 10), submittedBy: 1);

        await act.Should().ThrowAsync<ApproverResolutionException>().WithMessage("*no active head*");
    }
}

public class ApproverResolutionService_ManagerType
{
    private static (ApproverResolutionService svc, Mock<IOrgChartService> orgChart) Build()
    {
        var uq  = new Mock<IUserQueryService>();
        var org = new Mock<IOrgChartService>();
        return (new ApproverResolutionService(DbFactory.Create(), uq.Object, org.Object), org);
    }

    [Fact]
    public async Task MANAGER_type_returns_submitters_supervisor()
    {
        var (svc, org) = Build();
        org.Setup(o => o.GetSupervisorAsync(Fake.User1)).ReturnsAsync(new UserIdentity(Fake.User2, "Manager Bob"));

        var result = await svc.ResolveApproversAsync(
            Fake.Step(approverType: "MANAGER"), submittedBy: Fake.User1);

        result.Should().ContainSingle(a => a.UserId == Fake.User2);
    }

    [Fact]
    public async Task MANAGER_type_throws_ApproverResolutionException_when_no_supervisor()
    {
        var (svc, org) = Build();
        org.Setup(o => o.GetSupervisorAsync(It.IsAny<int>())).ReturnsAsync((UserIdentity?)null);

        var act = () => svc.ResolveApproversAsync(
            Fake.Step(approverType: "MANAGER"), submittedBy: Fake.User1);

        await act.Should().ThrowAsync<ApproverResolutionException>().WithMessage("*no active supervisor*");
    }
}

public class ApproverResolutionService_AnyType
{
    [Fact]
    public async Task ANY_type_returns_empty_approver_list()
    {
        var svc = new ApproverResolutionService(
            DbFactory.Create(),
            new Mock<IUserQueryService>().Object,
            new Mock<IOrgChartService>().Object);

        var result = await svc.ResolveApproversAsync(
            Fake.Step(approverType: "ANY", approverRefId: null), submittedBy: 1);

        result.Should().BeEmpty();
    }
}

public class ApproverResolutionService_GroupType
{
    private static ApproverResolutionService Build(
        Action<WorkflowDbContext>? seed,
        Mock<IUserQueryService>? uq = null)
    {
        uq ??= new Mock<IUserQueryService>();
        return new ApproverResolutionService(
            DbFactory.Create(seed),
            uq.Object,
            new Mock<IOrgChartService>().Object);
    }

    [Fact]
    public async Task GROUP_type_resolves_active_members_via_UserQueryService()
    {
        var uq = new Mock<IUserQueryService>();
        uq.Setup(u => u.GetUsersAsync(It.IsAny<IReadOnlyList<int>>()))
          .ReturnsAsync((IReadOnlyList<UserIdentity>)[
              new UserIdentity(Fake.User1, "Alice"),
              new UserIdentity(Fake.User2, "Bob")]);

        var svc = Build(db =>
        {
            db.WorkflowGroups.Add(new WorkflowGroup { Id = 7, UUID = Guid.NewGuid(), Name = "Finance", IsActive = true, CreatedBy = 1, CreatedDate = DateTime.UtcNow });
            db.WorkflowGroupMembers.AddRange(
                new WorkflowGroupMember { Id = 1, UUID = Guid.NewGuid(), GroupId = 7, UserId = Fake.User1, IsActive = true, AddedBy = 1, AddedDate = DateTime.UtcNow },
                new WorkflowGroupMember { Id = 2, UUID = Guid.NewGuid(), GroupId = 7, UserId = Fake.User2, IsActive = true, AddedBy = 1, AddedDate = DateTime.UtcNow });
        }, uq);

        var result = await svc.ResolveApproversAsync(
            Fake.Step(approverType: "GROUP", approverRefId: 7), submittedBy: 1);

        result.Should().HaveCount(2);
    }

    [Fact]
    public async Task GROUP_type_excludes_inactive_members()
    {
        var uq = new Mock<IUserQueryService>();
        uq.Setup(u => u.GetUsersAsync(It.Is<IReadOnlyList<int>>(ids => ids.Count == 1)))
          .ReturnsAsync((IReadOnlyList<UserIdentity>)[new UserIdentity(Fake.User1, "Alice")]);

        var svc = Build(db =>
        {
            db.WorkflowGroups.Add(new WorkflowGroup { Id = 7, UUID = Guid.NewGuid(), Name = "Finance", IsActive = true, CreatedBy = 1, CreatedDate = DateTime.UtcNow });
            db.WorkflowGroupMembers.AddRange(
                new WorkflowGroupMember { Id = 1, UUID = Guid.NewGuid(), GroupId = 7, UserId = Fake.User1, IsActive = true,  AddedBy = 1, AddedDate = DateTime.UtcNow },
                new WorkflowGroupMember { Id = 2, UUID = Guid.NewGuid(), GroupId = 7, UserId = Fake.User2, IsActive = false, AddedBy = 1, AddedDate = DateTime.UtcNow });
        }, uq);

        var result = await svc.ResolveApproversAsync(
            Fake.Step(approverType: "GROUP", approverRefId: 7), submittedBy: 1);

        result.Should().ContainSingle();
        uq.Verify(u => u.GetUsersAsync(It.Is<IReadOnlyList<int>>(ids => ids.Count == 1 && ids[0] == Fake.User1)), Times.Once);
    }

    [Fact]
    public async Task GROUP_type_throws_ApproverResolutionException_when_group_has_no_active_members()
    {
        var svc = Build(db =>
        {
            db.WorkflowGroups.Add(new WorkflowGroup { Id = 7, UUID = Guid.NewGuid(), Name = "Empty", IsActive = true, CreatedBy = 1, CreatedDate = DateTime.UtcNow });
            // no members
        });

        var act = () => svc.ResolveApproversAsync(
            Fake.Step(approverType: "GROUP", approverRefId: 7), submittedBy: 1);

        await act.Should().ThrowAsync<ApproverResolutionException>().WithMessage("*no active members*");
    }

    [Fact]
    public async Task Unknown_approver_type_throws_WorkflowConfigurationException()
    {
        var svc = Build(seed: null);

        var act = () => svc.ResolveApproversAsync(
            Fake.Step(approverType: "MAGIC"), submittedBy: 1);

        await act.Should().ThrowAsync<WorkflowConfigurationException>().WithMessage("*MAGIC*");
    }
}
