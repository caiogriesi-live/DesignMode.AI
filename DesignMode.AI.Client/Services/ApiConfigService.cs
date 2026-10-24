using Microsoft.AspNetCore.Components;

namespace DesignMode.AI.Client.Services
{
    public class ApiConfigService
    {
        private readonly NavigationManager _navigationManager;

        public string ApiBaseUrl { get; private set; }
        public string StorageBaseUrl { get; private set; }

        public ApiConfigService(NavigationManager navigationManager)
        {
            _navigationManager = navigationManager;
            SetApiBaseUrl();
            SetStorageBaseUrl();
        }

        private void SetApiBaseUrl()
        {
            var uri = new Uri(_navigationManager.Uri);

            if (uri.Host.Contains("localhost"))
            {
                ApiBaseUrl = "http://localhost:7032/";
            }
            else if (uri.Host.Contains("trycloudflare"))
            {
                ApiBaseUrl = Environment.GetEnvironmentVariable("AzureFunctionCloudAddress");
            }
            else
            {
                ApiBaseUrl = "https://designmodeaiserver20250401080115.azurewebsites.net/";
            }
        }

        private void SetStorageBaseUrl()
        {
            var uri = new Uri(_navigationManager.Uri);

            if (uri.Host.Contains("localhost"))
            {
                StorageBaseUrl = "http://localhost/";
            }
            else if (uri.Host.Contains("trycloudflare"))
            {
                StorageBaseUrl = "https://designmode-storage.s3.us-east-1.amazonaws.com/";
            }
            else
            {
                StorageBaseUrl = "https://designmode-storage.s3.us-east-1.amazonaws.com/";
            }
        }
    }
}
