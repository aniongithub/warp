using System;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Yarp.ReverseProxy.Forwarder;

namespace Warp
{
    public class TransformInterceptor : HttpTransformer
    {
        private readonly ILogger<TransformInterceptor> _logger;

        public TransformInterceptor(ILogger<TransformInterceptor> logger)
        {
            _logger = logger;
        }

        public override async ValueTask TransformRequestAsync(HttpContext context, HttpRequestMessage proxyRequest, string destinationPrefix, System.Threading.CancellationToken cancellationToken)
        {
            // Log the path before calling the base method
            _logger.LogInformation("[TransformInterceptor] Path before base call: {Path}", context.Request.Path);

            await base.TransformRequestAsync(context, proxyRequest, destinationPrefix, cancellationToken);

            // Log the path after calling the base method
            _logger.LogInformation("[TransformInterceptor] Path after base call: {Path}", context.Request.Path);

            // Log the destination URL
            _logger.LogInformation("[TransformInterceptor] Destination URL: {Destination}", proxyRequest.RequestUri?.ToString());
        }
    }
}
