using System;
using System.Collections.Generic;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RegardOrderBot.POCO;

namespace RegardOrderBot
{
	public class RegardParser
	{
		readonly ILogger<RegardParser> logger;
		readonly OrderBot orderBot;
		List<Product> products;
		public RegardParser(ILogger<RegardParser> logger, IHostedService hostedService)
		{
			this.logger = logger;
			orderBot = (OrderBot)hostedService;
		}

		public bool? Start()
		{
			bool? result = null;
			try
			{
				if (GetProducts())
				{
					products.ForEach((product) =>
					{
						logger.LogInformation($"[{DateTime.Now:dd.MM.yyy HH.mm.ss}] Отслеживаю товар ID: {product.ArtNumber} с максимальной ценой: {product.MaxPrice.ToString("C", OrderBot.culture)}");
					});
					result = true;
				}
				else
				{
					result = false;
					logger.LogInformation($"[{DateTime.Now:dd.MM.yyy HH.mm.ss}] Нет товаров для отслеживания!");
					orderBot.Host.StopApplication();
				}
				
			}
			catch (Exception ex)
			{
				logger.LogError(ex.Message);
			}

			return result;
		}

		private bool GetProducts()
		{
			if (orderBot == null)
				throw new Exception($"Ошибка! не удалось получить сервис {nameof(OrderBot)}");

			products = orderBot.Products;
			if (products.Count > 0)
				return true;
			return false;
		}
	}
}
