using System;
using System.Collections.Generic;
using Amazon.Lambda.Core;
using Amazon.IdentityManagement;
using Amazon.IdentityManagement.Model;
using Newtonsoft.Json.Linq;
using System.Threading.Tasks;
using System.Net.Http;

namespace CustomLambdaBackedCloudFormationResource
{
    public class CFExtensions
    {

        public object response { get; private set; }
        /// <summary>
        /// Default constructor that Lambda will invoke.
        /// </summary>
        /// 
        public CFExtensions()
        {
        }

        [LambdaSerializer(typeof(Amazon.Lambda.Serialization.Json.JsonSerializer))]
        public string CreateOpenIDProvider(JObject input, ILambdaContext context)
        {

            Console.WriteLine(input.ToString(Newtonsoft.Json.Formatting.Indented)); //Print Input

            JObject resourceProperties = input["ResourceProperties"].ToObject<JObject>();
            if (resourceProperties["ProviderURL"] == null)
            {
                return NotifyFailure(input, context, "ProviderURL property is required.");
            }

            string providerURL = resourceProperties["ProviderURL"].ToString();
            if (providerURL.Length == 0)
            {
                return NotifyFailure(input, context, "ProviderURL property is required.");
            }

            if (providerURL.Length > 255)
            {
                return NotifyFailure(input, context, "ProviderURL too long,  Maximum 255 characters.");
            }

            if (!providerURL.ToLower().StartsWith("https"))
            {
                return NotifyFailure(input, context, "ProviderURL must begin with \"https\".");
            }



            string[] audiences = new string[0];
            if (resourceProperties["Audiences"] != null)
            {
                audiences = resourceProperties["Audiences"].ToObject<string[]>();
            }


            string[] thumbprintList = new string[0];
            if (resourceProperties["ThumbprintList"] != null)
            {
                thumbprintList = resourceProperties["ThumbprintList"].ToObject<string[]>();
            }

            if (thumbprintList.Length == 0)
            {
                return NotifyFailure(input, context, "Thumbprint list cannot be empty");
            }


            string providerArn = "";

            string requestType = input["RequestType"].ToString();

            try
            {
                switch (requestType)
                {
                    case "Create":

                        providerArn = Create(providerURL, thumbprintList, audiences, context);

                        break;
                    case "Update":


                        Update(providerArn, providerURL, thumbprintList, audiences, context);

                        break;
                    case "Delete":

                        Delete(providerArn, context);

                        break;
                    default:
                        break;

                }
            }
            catch (Exception e)
            {

                var exception = e;
                while (exception != null)
                {
                    Console.Write(exception.Message);

                    exception = exception.InnerException;
                }

                return NotifyFailure(input, context, e.Message);
            }

            return NotifySuccess(providerArn, input, context);


        }

        private string NotifySuccess(string providerArn, JObject input, ILambdaContext context, string message = "Resource creation successful")
        {
            var data = new DataResponse()
            {
                Arn = providerArn,
                Message = message
            };
            return Notify("SUCCESS", data, input, context);
        }

        private string NotifyFailure(JObject input, ILambdaContext context, string message = "Resource creation failed")
        {
            var data = new DataResponse()
            {
                Arn = "",
                Message = message
            };

            string requestType = input["RequestType"].ToString();
            if (requestType == "Delete")
            {
                return Notify("SUCCESS", data, input, context);
                //make sure it can always delete successfully regardless of errors thrown.
            }

            return Notify("FAILED", data, input, context);
        }

        private string Notify(string response, DataResponse data, JObject input, ILambdaContext context)
        {
            string responseUrl = input["ResponseURL"].ToString();
            string stackId = input["StackId"].ToString();
            string requestId = input["RequestId"].ToString();
            string logicalResourceId = input["LogicalResourceId"].ToString();

            CloudFormationResponse cf = new CloudFormationResponse();
            cf.Status = response; //Values should be either SUCCESS or FAILED
            cf.PhysicalResourceId = context.LogStreamName;
            cf.StackId = stackId;
            cf.RequestId = requestId;
            cf.LogicalResourceId = logicalResourceId;

            //This can be the dataset you wish to return
            cf.Data = data;
            Console.WriteLine(JObject.FromObject(cf).ToString(Newtonsoft.Json.Formatting.Indented));

            var t = PostToS3Async(responseUrl, cf);
            t.Wait();
            return t.Result.ToString();
        }


        private void Delete(string providerArn, ILambdaContext context)
        {


            var client = new AmazonIdentityManagementServiceClient();

            var request = new DeleteOpenIDConnectProviderRequest();
            request.OpenIDConnectProviderArn = providerArn;

            var result = client.DeleteOpenIDConnectProviderAsync(request).Result;
        }

        private void Update(string providerArn, string url, string[] thumbprintList, string[] audiences, ILambdaContext context)
        {
            Delete(providerArn, context);
            Create(url, thumbprintList, audiences, context);
        }

        private string Create(string url, string[] thumbprintList, string[] audiences, ILambdaContext context)
        {


            var client = new AmazonIdentityManagementServiceClient();

            var request = new CreateOpenIDConnectProviderRequest();
            request.Url = url;// "test.accounts.google.com";
            request.ThumbprintList = new List<string>(thumbprintList);

            var result = client.CreateOpenIDConnectProviderAsync(request).Result;

            var providerArn = result.OpenIDConnectProviderArn;

            foreach (var audience in audiences)
            {
                AddAudience(providerArn, audience, context);
            }

            return providerArn;

        }

        private void AddAudience(string providerArn, string audience, ILambdaContext context)
        {
            var client = new AmazonIdentityManagementServiceClient();

            var request = new AddClientIDToOpenIDConnectProviderRequest();
            request.ClientID = audience;
            request.OpenIDConnectProviderArn = providerArn;

            var response = client.AddClientIDToOpenIDConnectProviderAsync(request).Result;
        }



        public async Task<bool> PostToS3Async(String presignedUrl, CloudFormationResponse data)
        {
            String jsonData = JObject.FromObject(data).ToString(Newtonsoft.Json.Formatting.None);
            StringContent val = new StringContent(jsonData);
            val.Headers.Clear();
            val.Headers.TryAddWithoutValidation("content-type", "");
            val.Headers.TryAddWithoutValidation("content-length", jsonData.Length.ToString());

            var client = new HttpClient();
            client.DefaultRequestHeaders.Clear();

            await client.PutAsync(presignedUrl, val);
            return true;

        }

        public class CloudFormationResponse
        {
            public string Status { get; set; }
            public string PhysicalResourceId { get; set; }
            public string StackId { get; set; }
            public string RequestId { get; set; }
            public string LogicalResourceId { get; set; }
            public DataResponse Data { get; set; }
        }

        public class DataResponse
        {
            public string Arn { get; set; }
            public string Message { get; set; }
        }

    }

}
