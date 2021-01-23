using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging;
using RegardOrderBot.POCO;

namespace RegardOrderBot
{
	public class RegardParser
	{
		readonly ILogger logger;
		readonly List<Product> products;
		public RegardParser(ILogger logger, List<Product> products)
		{
			this.logger = logger;
			this.products = products;
		}

		public void Start()
		{
			products.ForEach((product) =>
			{
				logger.LogInformation($"[{DateTime.Now:dd.MM.yyy HH.mm.ss}] Отслеживаю товар ID: {product.ArtNumber} с максимальной ценой: {product.MaxPrice.ToString("C", OrderBot.culture)}");
			});
		}
	}
}
