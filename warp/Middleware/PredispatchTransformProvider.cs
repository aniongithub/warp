using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Yarp.ReverseProxy.Transforms;
using Yarp.ReverseProxy.Transforms.Builder;
using Yarp.ReverseProxy.Model;

namespace Warp.Middleware
{
    public class PredispatchTransformProvider : ITransformProvider
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IDictionary<string, Func<RequestDelegate, RequestDelegate>> _componentMap;

        public PredispatchTransformProvider(IServiceProvider serviceProvider, IDictionary<string, Func<RequestDelegate, RequestDelegate>> componentMap)
        {
            _serviceProvider = serviceProvider;
            _componentMap = componentMap;
        }

        public void Apply(TransformBuilderContext context)
        {
            var metadata = context.Route.Metadata;
            if (metadata != null && metadata.TryGetValue("Predispatch", out var pre) && pre is string preStr)
            {
                var predispatch = preStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (predispatch.Length > 0)
                {
                    var logger = _serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("PredispatchPipelineTransform");
                    var predispatchMiddleware = new List<Func<RequestDelegate, RequestDelegate>>();
                    foreach (var name in predispatch)
                    {
                        if (_componentMap.TryGetValue(name, out var middleware))
                        {
                            predispatchMiddleware.Add(middleware);
                        }
                    }
                    var routeId = context.Route.RouteId ?? "unknown";
                    context.RequestTransforms.Add(new PredispatchPipelineTransform(predispatchMiddleware, logger, routeId));
                }
            }
        }

        public void ValidateRoute(TransformRouteValidationContext context)
        {
            // No-op for now; implement validation if needed
        }

        public void ValidateCluster(TransformClusterValidationContext context)
        {
            // No-op for now; implement validation if needed
        }
    }
}
