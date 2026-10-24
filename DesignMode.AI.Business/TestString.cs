namespace DesignMode.AI.Business
{
    using Microsoft.Extensions.Options;
    using DesignMode.AI.Shared.Interfaces.Business;
    using DesignMode.AI.Common;
    using DesignMode.AI.Shared.Interfaces.Data;
    using DesignMode.AI.Shared.Models;
    using DesignMode.AI.Services;
    using DesignMode.AI.Services.OpenAI;
    using DesignMode.AI.Services.OpenAI.Models;
    using DesignMode.AI.Services.OpenAI.Models.Settings;

    public class TestString : ITestString
    {
        private readonly IOpenAIService openAIService;
        private readonly ITestDac testDac;

        public TestString(IOpenAIService openAIService,
            ITestDac testDac)
        {
            this.openAIService = openAIService ?? throw new ArgumentNullException(nameof(openAIService));
            this.testDac = testDac ?? throw new ArgumentNullException(nameof(testDac));
        }

        public async Task<Response<TestModel>> TestStringResponse()
        {
            var testDac = this.testDac.GetTest();
            return new Response<TestModel>
            {
                Item = new TestModel { TestText = "Test string 123 caio 1021am " + testDac.Item }
            };
        }

        public async Task<Response<OpenAIResponse>> GetAIResponse()
        {
            var request = new OpenAIRequest { Prompt = "list 7 animals" };

            try
            {
                return await this.openAIService.GetOpenAIResponse(request);
            }
            catch (Exception ex)
            {
                return new Response<OpenAIResponse>
                {
                    Item = new OpenAIResponse { Response = $"Error: {ex.Message}" }
                };
            }
        }
    }


}
