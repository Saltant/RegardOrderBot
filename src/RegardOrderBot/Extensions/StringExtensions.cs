using System;
using System.Linq;

namespace RegardOrderBot.Extensions
{
	public static class StringExtensions
	{
		public static int ToInt(this string str)
		{
			int result;
			string parsed = new string(str.Where(x => char.IsDigit(x)).ToArray());
			if (!string.IsNullOrEmpty(parsed))
				result = Convert.ToInt32(parsed);
			else
				throw new Exception($"Ошибка! Аргумент {(string.IsNullOrEmpty(str) == true ? "не может быть пустым!" : $"'{str}' не является числом!")}");

			return result;
		}
	}
}
