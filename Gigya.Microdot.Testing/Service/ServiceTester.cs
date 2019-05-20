﻿#region Copyright 
// Copyright 2017 Gigya Inc.  All rights reserved.
// 
// Licensed under the Apache License, Version 2.0 (the "License"); 
// you may not use this file except in compliance with the License.  
// You may obtain a copy of the License at
// 
//     http://www.apache.org/licenses/LICENSE-2.0
// 
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDER AND CONTRIBUTORS "AS IS"
// AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
// IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
// ARE DISCLAIMED.  IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE
// LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR
// CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF
// SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS
// INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN
// CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
// ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
// POSSIBILITY OF SUCH DAMAGE.
#endregion

using System;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using Gigya.Common.Contracts.HttpService;
using Gigya.Microdot.Fakes;
using Gigya.Microdot.Hosting.Service;
using Gigya.Microdot.Orleans.Hosting;
using Gigya.Microdot.Orleans.Hosting.Logging;
using Gigya.Microdot.Orleans.Ninject.Host;
using Gigya.Microdot.SharedLogic;
using Gigya.Microdot.Testing.Shared.Service;
using Microsoft.Extensions.Logging;
using Ninject;
using Ninject.Syntax;
using Orleans;
using Orleans.Configuration;

namespace Gigya.Microdot.Testing.Service
{
    public class ServiceTester<TServiceHost> : ServiceTesterBase where TServiceHost : MicrodotOrleansServiceHost, new()
    {
        public TServiceHost Host { get; }
        public Task SiloStopped { get; }

        public IClusterClient _clusterClient;

        public object _locker = new object();
        public IClusterClient GrainClient
        {
            get
            {
                InitGrainClient(timeout: TimeSpan.FromSeconds(10));
                return _clusterClient;
            }
        }

        public ServiceTester(IResolutionRoot resolutionRoot, ServiceArguments serviceArguments)
        {
            ResolutionRoot = resolutionRoot;
            BasePort = serviceArguments.BasePortOverride ?? GetBasePortFromHttpServiceAttribute();

            Host = new TServiceHost();
            SiloStopped = Task.Run(() => Host.Run(serviceArguments));

            //Silo is ready or failed to start
            Task.WaitAny(SiloStopped, Host.WaitForServiceStartedAsync());
        }
        protected int GetBasePortFromHttpServiceAttribute()
        {
            var commonConfig = new BaseCommonConfig();
            var mapper = new OrleansServiceInterfaceMapper(new AssemblyProvider(new ApplicationDirectoryProvider(commonConfig), commonConfig, new ConsoleLog()));
            var basePort = mapper.ServiceInterfaceTypes.First().GetCustomAttribute<HttpServiceAttribute>().BasePort;

            return basePort;
        }

        public override void Dispose()
        {
            _clusterClient?.Dispose();

            Host.Stop(); //don't use host.dispose, host.stop should do all the work

            var completed = SiloStopped.Wait(60000);

            if (!completed)
                throw new TimeoutException(
                    "ServiceTester: The service failed to shutdown within the 60 second limit.");

            if (Host.WaitForServiceGracefullyStoppedAsync().IsCompleted &&
                Host.WaitForServiceGracefullyStoppedAsync().Result == StopResult.Force)
                throw new TimeoutException("ServiceTester: The service failed to shutdown gracefully.");
        }

        protected virtual IClusterClient InitGrainClient(TimeSpan? timeout)
        {
            if (_clusterClient == null)
            {
                lock (_locker)
                {
                    if (_clusterClient != null) return _clusterClient;


                    ClientBuilder grainClientBuilder = new ClientBuilder();
                    grainClientBuilder.Configure<EndpointOptions>(options =>
                    {
                        options.AdvertisedIPAddress = IPAddress.Loopback;
                        options.GatewayPort = BasePort + (int)PortOffsets.SiloGateway;
                    });

                    IServiceProviderInit serviceProvider = ResolutionRoot.Get<IServiceProviderInit>();
                    OrleansLogProvider logProvider = ResolutionRoot.Get<OrleansLogProvider>();

                    grainClientBuilder.UseServiceProviderFactory(serviceProvider.ConfigureServices)
                        .ConfigureLogging(op => op.AddProvider(logProvider));

                    var grainClient = grainClientBuilder.Build();
                    grainClient.Connect();
                    _clusterClient = grainClient;

                }
            }
            return _clusterClient;

        }
    }

    public static class ServiceTesterExtensions
    {
        public static ServiceTester<TServiceHost> GetServiceTester<TServiceHost>(this IResolutionRoot resolutionRoot, ServiceArguments serviceArguments)
            where TServiceHost : MicrodotOrleansServiceHost, new()
        {
            return resolutionRoot.Get<Func<ServiceArguments, ServiceTester<TServiceHost>>>()(serviceArguments);

        }


        public static ServiceTester<TServiceHost> GetServiceTester<TServiceHost>(this IResolutionRoot resolutionRoot, int port)
            where TServiceHost : MicrodotOrleansServiceHost, new()
        {
            return resolutionRoot.Get<Func<ServiceArguments, ServiceTester<TServiceHost>>>()(new ServiceArguments(ServiceStartupMode.CommandLineInteractive, ConsoleOutputMode.Disabled, SiloClusterMode.PrimaryNode, port));

        }
    }
}
