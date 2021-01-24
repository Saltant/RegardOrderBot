using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RegardOrderBot.Extensions;
using RegardOrderBot.POCO;
using System;
using System.Collections.Generic;
using System.Globalization;
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
		readonly IServiceProvider serviceProvider;
		readonly public static CultureInfo culture = CultureInfo.CreateSpecificCulture("ru-RU");
		RegardParser regardParser;
		int artNumber;
		int maxPrice;
		public IHostApplicationLifetime Host
		{
			get
			{
				return hostAppLifetime;
			}
		}
		public int ProductId
		{
			get
			{
				return artNumber;
			}
		}
		public int ProductMaxPrice
		{
			get
			{
				return maxPrice;
			}
		}
		public List<Product> Products
		{
			get
			{
				return products;
			}
		}
		public OrderBot(ILogger<OrderBot> logger, IHostApplicationLifetime hostAppLifetime, IConfiguration configuration, IServiceProvider serviceProvider)
		{
			this.logger = logger;
			this.hostAppLifetime = hostAppLifetime;
			this.serviceProvider = serviceProvider;
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
			if(commandLineArgs["art"] == null || commandLineArgs["maxprice"] == null)
				return false;

			artNumber = commandLineArgs["art"].ToInt();
			maxPrice = commandLineArgs["maxprice"].ToInt();
			products.Add(new Product { ArtNumber = artNumber, MaxPrice = maxPrice });
			return true;
		}

		public Task StartAsync(CancellationToken cancellationToken)
		{
			regardParser = serviceProvider.GetService<RegardParser>();
			bool? isParserStarted = regardParser?.Start();
			if (isParserStarted == null)
			{
				logger.LogError($"Ошибка! не удалось получить сервис {nameof(RegardParser)}");
				hostAppLifetime.StopApplication();
			}else if (isParserStarted == true)
			{
				logger.LogInformation($"[{DateTime.Now:dd.MM.yyy HH.mm.ss}] Парсер товаров успешно запущен.");
			}

			return Task.CompletedTask;
		}

		public Task StopAsync(CancellationToken cancellationToken)
		{
			logger.LogInformation("Приложение остановлено.");
			return Task.CompletedTask;
		}
	}
}
