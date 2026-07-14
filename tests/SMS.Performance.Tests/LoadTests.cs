using Xunit;

namespace SMS.Performance.Tests;

public class LoadTests
{
    [Fact(Skip = "Run manually against a live environment")]
    public void Auth_Login_Endpoint_LoadTest()
    {
        // TODO: Implement using NBomber
        // var scenario = Scenario.Create("auth_login", async ctx =>
        // {
        //     using var client = new HttpClient();
        //     var response = await client.PostAsJsonAsync("http://localhost:5000/api/auth/login", ...);
        //     return response.IsSuccessStatusCode ? Response.Ok() : Response.Fail();
        // })
        // .WithLoadSimulations(Simulation.Inject(rate: 50, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(30)));
        //
        // NBomberRunner.RegisterScenarios(scenario).Run();
        Assert.True(true, "Placeholder load test stub.");
    }
}
