using SMS.Integration.Tests.Workflow.Infrastructure;
using Xunit;

namespace SMS.Integration.Tests.Workflow;

[CollectionDefinition("WorkflowIntegration")]
public sealed class WorkflowTestCollection : ICollectionFixture<WorkflowWebApplicationFactory>;
