using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RegardOrderBot.Extensions;
using RegardOrderBot.POCO;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RegardOrderBot
{
	public class OrderBot : IHostedService
	{
		readonly ILogger<OrderBot> logger;
		readonly IHostApplicationLifetime hostAppLifetime;
		readonly List<Product> products = new List<Product>();
		readonly bool isHaveCommandLineArgs;
		readonly public static CultureInfo culture = CultureInfo.CreateSpecificCulture("ru-RU");
		int id;
		int maxPrice;
		public int ProductId
		{
			get
			{
				return id;
			}
		}
		public int ProductMaxPrice
		{
			get
			{
				return maxPrice;
			}
		}
		public OrderBot(ILogger<OrderBot> logger, IHostApplicationLifetime hostAppLifetime, IConfiguration configuration)
		{
			this.logger = logger;
			this.hostAppLifetime = hostAppLifetime;
			culture.NumberFormat.CurrencySymbol = "руб.";
			try
			{
				isHaveCommandLineArgs = ReadCommandLineArgs(configuration);
			}
			catch(Exception ex)
			{
				logger.LogError(ex.Message);
			}
			finally
			{
				if (!isHaveCommandLineArgs)
					configuration.GetSection("Products").Bind(products);
			}
		}

		private bool ReadCommandLineArgs(IConfiguration commandLineArgs)
		{
			if(commandLineArgs["id"] == null || commandLineArgs["maxprice"] == null)
				return false;

			id = commandLineArgs["id"].ToInt();
			maxPrice = commandLineArgs["maxprice"].ToInt();			
			return true;
		}

		public Task StartAsync(CancellationToken cancellationToken)
		{
			if (isHaveCommandLineArgs)
				products.Add(new Product { ArtNumber = id, MaxPrice = maxPrice });

			RegardParser regardParser = new RegardParser(logger, products);
			regardParser.Start();

			return Task.CompletedTask;
		}

		public Task StopAsync(CancellationToken cancellationToken)
		{
			hostAppLifetime.StopApplication();
			logger.LogInformation("Приложение остановлено.");

			return Task.CompletedTask;
		}
	}
}
