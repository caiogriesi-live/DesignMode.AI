namespace DesignMode.AI.Client.Pages.Init
{
    using Microsoft.AspNetCore.Components;
    using static System.Net.Mime.MediaTypeNames;
    using Telerik.Blazor.Components;
    using Telerik.Blazor;
    using Telerik.SvgIcons;
    using DesignMode.AI.Client.Services;

    public partial class Init : ComponentBase
    {
        private string result;

        [Inject] private NavigationManager Navigation { get; set; }
        [Inject] private ApiConfigService ApiConfig { get; set; }

        //private async Task OnClickHandler()
        //{
        //    result = DateTime.Now.ToString();
        //}
        //private async Task Skip()
        //{
        //    IsSkipButtonDisabled = false;
        //    IsNextButtonDisabled = false;
        //    //LoaderVisible = true;
        //    Navigation.NavigateTo($"init/idea");
        //}

        ////bool LoaderVisible { get; set; } = false;
        //bool IsNextButtonDisabled { get; set; } = true;
        //bool IsSkipButtonDisabled { get; set; } = true;

        //private static readonly HttpClient httpClient = new HttpClient();

        //private async Task Next()
        //{
        //    IsSkipButtonDisabled = false;
        //    IsNextButtonDisabled = false;
        //    LoaderVisible = true;

        //    // Simulating an async operation
        //    await Task.Delay(2000);

        //    // Add your logic here (e.g., navigation or form submission)
        //    var test = organizationText;
        //    var test2 = industryText;

        //    try
        //    {
        //        // Do not read the content if you don't need it
        //        //Console.WriteLine("Success: Operation completed successfully.");
        //        Navigation.NavigateTo($"init/idea?name={Uri.EscapeDataString(test)}&industry={Uri.EscapeDataString(test2)}");
        //    }
        //    catch (Exception ex)
        //    {
        //        //Console.WriteLine("Exception occurred: " + ex.Message);
        //    }
        //}

        //protected override async Task OnInitializedAsync()
        //{
        //    var authState = await AuthStateProvider.GetAuthenticationStateAsync();
        //    var user = authState.User;

        //    if (user.Identity is not { IsAuthenticated: true })
        //    {
        //        Navigation.NavigateTo("/login", replace: true);
        //        return;
        //    }
        //}
        //private ElementReference myref;
        //private string organizationText;
        //private string industryText;
    }
}
