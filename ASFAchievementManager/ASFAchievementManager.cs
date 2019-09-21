﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Composition;
using System.Linq;
using System.Threading.Tasks;
using ArchiSteamFarm;
using ArchiSteamFarm.Localization;
using ArchiSteamFarm.Plugins;
using JetBrains.Annotations;
using SteamKit2;

namespace ASFAchievementManager {
	[Export(typeof(IPlugin))]
	// ReSharper disable once UnusedMember.Global
	public sealed class ASFAchievementManager : IBotSteamClient, IBotCommand {
		private static readonly ConcurrentDictionary<Bot, AchievementHandler> AchievementHandlers = new ConcurrentDictionary<Bot, AchievementHandler>();

		public async Task<string> OnBotCommand([NotNull] Bot bot, ulong steamID, [NotNull] string message, string[] args) {
			if (!bot.HasPermission(steamID, BotConfig.EPermission.Master)) {
				return null;
			}

			switch (args.Length) {
				case 0:
					bot.ArchiLogger.LogNullError(nameof(args));
					return null;
				case 1:
					return null;
				default:
					switch (args[0].ToUpperInvariant()) {
						case "ALIST" when args.Length > 2:
							return await ResponseAchievementList(args[1], Utilities.GetArgsAsText(args, 2, ",")).ConfigureAwait(false);
						case "ALIST":
							return await ResponseAchievementList(bot, args[1]).ConfigureAwait(false);
						case "ASET" when args.Length > 3:
							return await ResponseAchievementSet(args[1], args[2], Utilities.GetArgsAsText(args, 3, ",")).ConfigureAwait(false);
						case "ASET" when args.Length > 2:
							return await ResponseAchievementSet(bot, args[1], Utilities.GetArgsAsText(args, 2, ",")).ConfigureAwait(false);
						case "ARESET" when args.Length > 3:
							return await ResponseAchievementSet(args[1], args[2], Utilities.GetArgsAsText(args, 3, ","), false).ConfigureAwait(false);
						case "ARESET" when args.Length > 2:
							return await ResponseAchievementSet(bot, args[1], Utilities.GetArgsAsText(args, 2, ","), false).ConfigureAwait(false);
						default:
							return null;
					}
			}
		}

		public string Name => "ASF Achievement Manager";
		public Version Version => typeof(ASFAchievementManager).Assembly.GetName().Version;

		public void OnLoaded() => ASF.ArchiLogger.LogGenericInfo("ASF Achievement Manager Plugin by Ryzhehvost, powered by ginger cats|Fork by Vital7");

		public void OnBotSteamCallbacksInit([NotNull] Bot bot, [NotNull] CallbackManager callbackManager) {
		}

		public IReadOnlyCollection<ClientMsgHandler> OnBotSteamHandlersInit([NotNull] Bot bot) {
			AchievementHandler currentBotAchievementHandler = new AchievementHandler();
			AchievementHandlers.TryAdd(bot, currentBotAchievementHandler);
			return new[] {currentBotAchievementHandler};
		}

		#region Responses

		private static async Task<string> ResponseAchievementList(Bot bot, string appids) {
			string[] gameIDs = appids.Split(new[] {','}, StringSplitOptions.RemoveEmptyEntries);

			if (gameIDs.Length == 0) {
				return bot.Commands.FormatBotResponse(string.Format(Strings.ErrorIsEmpty, nameof(gameIDs)));
			}

			if (AchievementHandlers.TryGetValue(bot, out AchievementHandler achievementHandler)) {
				HashSet<uint> gamesToGetAchievements = new HashSet<uint>();

				foreach (string game in gameIDs) {
					if (!uint.TryParse(game, out uint gameID) || (gameID == 0)) {
						return bot.Commands.FormatBotResponse(string.Format(Strings.ErrorParsingObject, nameof(gameID)));
					}

					gamesToGetAchievements.Add(gameID);
				}


				IList<string> results = await Utilities.InParallel(gamesToGetAchievements.Select(appID => Task.Run(() => achievementHandler.GetAchievements(bot, appID)))).ConfigureAwait(false);
				List<string> responses = new List<string>(results.Where(result => !string.IsNullOrEmpty(result)));

				return responses.Count > 0 ? bot.Commands.FormatBotResponse(string.Join(Environment.NewLine, responses)) : null;
			}

			return bot.Commands.FormatBotResponse(string.Format(Strings.ErrorIsEmpty, nameof(AchievementHandlers)));
		}

		private static async Task<string> ResponseAchievementList(string botNames, string appids) {
			HashSet<Bot> bots = Bot.GetBots(botNames);
			if ((bots == null) || (bots.Count == 0)) {
				return Commands.FormatStaticResponse(string.Format(Strings.BotNotFound, botNames));
			}

			IList<string> results = await Utilities.InParallel(bots.Select(bot => ResponseAchievementList(bot, appids))).ConfigureAwait(false);
			List<string> responses = new List<string>(results.Where(result => !string.IsNullOrEmpty(result)));

			return responses.Count > 0 ? string.Join(Environment.NewLine, responses) : null;
		}

		private static async Task<string> ResponseAchievementSet(Bot bot, string appid, string AchievementNumbers, bool set = true) {
			if (string.IsNullOrEmpty(AchievementNumbers)) {
				return bot.Commands.FormatBotResponse(string.Format(Strings.ErrorObjectIsNull, nameof(AchievementNumbers)));
			}

			if (!uint.TryParse(appid, out uint appId)) {
				return bot.Commands.FormatBotResponse(string.Format(Strings.ErrorIsInvalid, nameof(appId)));
			}

			if (!AchievementHandlers.TryGetValue(bot, out AchievementHandler AchievementHandler)) {
				return bot.Commands.FormatBotResponse(string.Format(Strings.ErrorIsEmpty, nameof(AchievementHandlers)));
			}

			HashSet<uint> achievements = new HashSet<uint>();

			string[] achievementStrings = AchievementNumbers.Split(new[] {','}, StringSplitOptions.RemoveEmptyEntries);

			if (!AchievementNumbers.Equals("*")) {
				foreach (string achievement in achievementStrings) {
					if (!uint.TryParse(achievement, out uint achievementNumber) || (achievementNumber == 0)) {
						return bot.Commands.FormatBotResponse(string.Format(Strings.ErrorParsingObject, achievement));
					}

					achievements.Add(achievementNumber);
				}

				if (achievements.Count == 0) {
					return bot.Commands.FormatBotResponse(string.Format(Strings.ErrorIsEmpty, "Achievements list"));
				}
			}

			return bot.Commands.FormatBotResponse(await Task.Run(() => AchievementHandler.SetAchievements(bot, appId, achievements, set)).ConfigureAwait(false));
		}

		private static async Task<string> ResponseAchievementSet(string botNames, string appid, string AchievementNumbers, bool set = true) {
			HashSet<Bot> bots = Bot.GetBots(botNames);
			if ((bots == null) || (bots.Count == 0)) {
				return Commands.FormatStaticResponse(string.Format(Strings.BotNotFound, botNames));
			}

			IList<string> results = await Utilities.InParallel(bots.Select(bot => ResponseAchievementSet(bot, appid, AchievementNumbers, set))).ConfigureAwait(false);
			List<string> responses = new List<string>(results.Where(result => !string.IsNullOrEmpty(result)));

			return responses.Count > 0 ? string.Join(Environment.NewLine, responses) : null;
		}
		
		#endregion
	}
}
