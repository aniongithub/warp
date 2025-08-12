using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Yarp.ReverseProxy.Transforms;

namespace Warp.Dilithium.Middleware
{
    /// <summary>
    /// Dummy transform that runs the Predispatch pipeline (middleware) after YARP transforms, before dispatch.
    /// </summary>
    public class PredispatchPipelineTransform : RequestTransform
    {
        private readonly IList<Func<RequestDelegate, RequestDelegate>> _predispatchMiddleware;
        private readonly ILogger _logger;
        private readonly string _routeId;

        public PredispatchPipelineTransform(
            IList<Func<RequestDelegate, RequestDelegate>> predispatchMiddleware,
            ILogger logger,
            string routeId)
        {
            _predispatchMiddleware = predispatchMiddleware;
            _logger = logger;
            _routeId = routeId;
        }

        public override async ValueTask ApplyAsync(RequestTransformContext context)
        {
            // Compose the pipeline for this request
            RequestDelegate terminal = _ => Task.CompletedTask;
            var pipeline = terminal;
            foreach (var middleware in ((IEnumerable<Func<RequestDelegate, RequestDelegate>>)_predispatchMiddleware).Reverse())
            {
                pipeline = middleware(pipeline);
            }
            _logger.LogDebug("Running Predispatch pipeline for route {RouteId}", _routeId);
            await pipeline(context.HttpContext);
        }
    }
}
