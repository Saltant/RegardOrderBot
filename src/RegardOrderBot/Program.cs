using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace RegardOrderBot
{
	class Program
	{
		static void Main(string[] args)
		{
			CreateHostBuilder(args).Build().Run();
		}

		public static IHostBuilder CreateHostBuilder(string[] args) =>
			Host.CreateDefaultBuilder(args)
				.UseSystemd()
				.ConfigureHostConfiguration(configHost => 
				{
					configHost.AddCommandLine(args);
				})
				.ConfigureAppConfiguration(appConfig =>
				{
					appConfig.AddJsonFile("products.json", true, true);
					appConfig.AddJsonFile("appsettings.json", false, true);
				})
				.ConfigureServices((hostContext, services) =>
				{
					services.AddSingleton<IHostedService, OrderBot>().BuildServiceProvider();
					services.AddSingleton<RegardParser>().BuildServiceProvider();
				});
	}
}
