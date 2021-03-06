﻿// Copyright (c) Microsoft Corporation
// All rights reserved.
// 
// Licensed under the Apache License, Version 2.0 (the "License"); you may not
// use this file except in compliance with the License.  You may obtain a copy
// of the License at http://www.apache.org/licenses/LICENSE-2.0
// 
// THIS CODE IS PROVIDED *AS IS* BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY
// KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION ANY IMPLIED
// WARRANTIES OR CONDITIONS OF TITLE, FITNESS FOR A PARTICULAR PURPOSE,
// MERCHANTABLITY OR NON-INFRINGEMENT.
// 
// See the Apache Version 2.0 License for specific language governing
// permissions and limitations under the License.

namespace Microsoft.WindowsAzure.Management.HDInsight.ClusterProvisioning.AzureManagementClient
{
    using System;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Threading.Tasks;
    using Microsoft.WindowsAzure.Management.Framework;
    using Microsoft.WindowsAzure.Management.Framework.InversionOfControl;
    using Microsoft.WindowsAzure.Management.Framework.WebRequest;
    using Microsoft.WindowsAzure.Management.HDInsight.ClusterProvisioning.Data;
    using Microsoft.WindowsAzure.Management.HDInsight.ClusterProvisioning.RestClient;
    using Microsoft.WindowsAzure.Management.HDInsight.ConnectionContext;

    internal class SubscriptionRegistrationClient : ISubscriptionRegistrationClient
    {
        private readonly IConnectionCredentials credentials;

        internal SubscriptionRegistrationClient(IConnectionCredentials credentials)
        {
            this.credentials = credentials;
        }

        // Method = "PUT", UriTemplate = "{subscriptionId}/services?service={namespace}.containers&action={action}
        public async Task RegisterSubscription()
        {
            // Creates an HTTP client
            using (var client = ServiceLocator.Instance.Locate<IHttpClientAbstractionFactory>().Create(this.credentials.Certificate))
            {
                // Creates the request
                string relativeUri = string.Format(
                    "{0}/services?service={1}.containers&action={2}",
                    this.credentials.SubscriptionId,
                    this.credentials.DeploymentNamespace,
                    "register");
                client.RequestUri = new Uri(this.credentials.Endpoint, new Uri(relativeUri, UriKind.Relative));
                client.RequestHeaders.Add(HDInsightRestHardcodes.XMsVersion);
                client.RequestHeaders.Add(HDInsightRestHardcodes.Accept);
                client.Content = string.Empty;
                client.Method = HttpMethod.Put;

                // Sends, validates and parses the response
                var httpResponse = await client.SendAsync();
                if (httpResponse.StatusCode == HttpStatusCode.OK || httpResponse.StatusCode == HttpStatusCode.Accepted)
                {
                    return;
                }

                // Returns conflict if it was already registered; adding a different if in case we want to log it eventually.
                if (httpResponse.StatusCode == HttpStatusCode.Conflict)
                {
                    return;
                }

                throw new HDInsightRestClientException(httpResponse.StatusCode, httpResponse.Content);
            }
        }
        
        public async Task<bool> ValidateSubscriptionLocation(string location)
        {
            var ret = await this.ManageSubscriptionLocation(HttpMethod.Get, location);
            if (ret.Item1 == HttpStatusCode.OK || ret.Item1 == HttpStatusCode.Accepted)
            {
                return true;
            }
            if (ret.Item1 == HttpStatusCode.NotFound)
            {
                return false;
            }

            throw new HDInsightRestClientException(ret.Item1, ret.Item2);
        }

        private const string RegisterSubscriptionPayload = 
"<CloudService xmlns=\"http://schemas.microsoft.com/windowsazure\">" +
    "<Label>HdInsight CloudService</Label>" +
    "<Description>HdInsight clusters for subscription {0}</Description>" +
    "<GeoRegion>{1}</GeoRegion>" +
"</CloudService>";

        public async Task RegisterSubscriptionLocation(string location)
        {
            var payload = string.Format(RegisterSubscriptionPayload, this.credentials.SubscriptionId, location);
            var ret = await this.ManageSubscriptionLocation(HttpMethod.Put, location, payload);
            if (ret.Item1 == HttpStatusCode.OK || ret.Item1 == HttpStatusCode.Accepted)
            {
                return;
            }

            // There might be race condition that register the subscription via other threads. Therefore, we will validate 
            // if the request went through despite error
            if (!await this.ValidateSubscriptionLocation(location))
            {
                throw new HDInsightRestClientException(ret.Item1, ret.Item2);
            }
        }

        public async Task UnregisterSubscriptionLocation(string location)
        {
            using (var managementClient = ServiceLocator.Instance.Locate<IHDInsightManagementRestClientFactory>().Create(this.credentials))
            {
                var payload = await managementClient.ListCloudServices();
                var clusters = PayloadConverter.DeserializeListContainersResult(payload, this.credentials.DeploymentNamespace);
                if (clusters.Any(cluster => cluster.Location == location))
                {
                    throw new InvalidOperationException("Cannot unregister a subscription location if it contains clusters");
                }
            }

            var ret = await this.ManageSubscriptionLocation(HttpMethod.Delete, location);
            if (ret.Item1 == HttpStatusCode.OK || ret.Item1 == HttpStatusCode.Accepted)
            {
                return;
            }

            throw new HDInsightRestClientException(ret.Item1, ret.Item2);
        }

        // Method = "{method}", UriTemplate = "{subscriptionId}/cloudservices/{resourceProviderNamespace}"
        internal async Task<Tuple<HttpStatusCode, string>> ManageSubscriptionLocation(HttpMethod method, string location, string payload = null)
        {
            var resolver = ServiceLocator.Instance.Locate<ICloudServiceNameResolver>();
            string regionCloudServicename = resolver.GetCloudServiceName(this.credentials.SubscriptionId, 
                                                                         this.credentials.DeploymentNamespace, 
                                                                         location);

            // Creates an HTTP client
            using (var client = ServiceLocator.Instance.Locate<IHttpClientAbstractionFactory>().Create(this.credentials.Certificate))
            {
                // Creates the request
                string relativeUri = string.Format("{0}/cloudservices/{1}", this.credentials.SubscriptionId, regionCloudServicename);
                client.RequestUri = new Uri(this.credentials.Endpoint, new Uri(relativeUri, UriKind.Relative));

                client.RequestHeaders.Add(HDInsightRestHardcodes.XMsVersion);
                client.RequestHeaders.Add(HDInsightRestHardcodes.Accept);
                client.Content = payload;
                client.Method = method;

                // Sends, validates and parses the response
                var httpResponse = await client.SendAsync();
                return new Tuple<HttpStatusCode, string>(httpResponse.StatusCode, httpResponse.Content);
            }
        }
    }
}