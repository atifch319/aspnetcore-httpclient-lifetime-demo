using AspNetCoreHttpClientLifetimeDemo.Models;
using AspNetCoreHttpClientLifetimeDemo.Services;
using Microsoft.AspNetCore.Mvc;

namespace AspNetCoreHttpClientLifetimeDemo.Controllers;

[ApiController]
[Route("api/downstream")]
public sealed class DownstreamController(DownstreamService downstreamService) : ControllerBase
{
    [HttpGet("sample")]
    [ProducesResponseType(typeof(DownstreamResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status504GatewayTimeout)]
    public async Task<ActionResult<DownstreamResponse>> GetSample(CancellationToken cancellationToken)
    {
        DownstreamResponse response = await downstreamService.GetSampleAsync(cancellationToken);
        return Ok(response);
    }
}
