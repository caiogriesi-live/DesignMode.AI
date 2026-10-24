using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DesignMode.AI.Common;
using DesignMode.AI.Shared;
using DesignMode.AI.Shared.Models;
using DesignMode.AI.Services.OpenAI;
using DesignMode.AI.Services.OpenAI.Models;
using DesignMode.AI.Services.OpenAI.Models.Settings;


namespace DesignMode.AI.Shared.Interfaces.Business
{
    public interface ITestString
    {
        /// <summary>
        /// Creates a test string to be consumed by an Azure Function.
        /// </summary>
        /// <param name="request">The request.</param>
        /// <returns>A Task.</returns>
        public Task<Response<TestModel>> TestStringResponse();

        /// <summary>
        /// Creates a test string to be consumed by an Azure Function.
        /// </summary>
        /// <param name="request">The request.</param>
        /// <returns>A Task.</returns>
        public Task<Response<OpenAIResponse>> GetAIResponse();
    }
}