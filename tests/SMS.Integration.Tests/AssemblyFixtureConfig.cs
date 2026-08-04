using Xunit;

// MT-008: xUnit runs different [Collection]s in parallel by default. This assembly hosts several
// WebApplicationFactory<Program> subclasses (WorkflowWebApplicationFactory's Docker-backed
// Testcontainers instance, and MultiTenancyIsolationFixture's plain in-process instance) that each
// boot the real Program.cs — including its process-wide static state such as Hangfire's
// GlobalConfiguration. Running two of these concurrently causes cross-collection interference
// (observed as MultiTenancy tests failing only when the Docker-dependent Workflow collection also
// runs in the same test process, never in isolation). Disabling parallelization here trades some
// wall-clock time for reliability, which matters far more for integration tests hitting a real DB.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
