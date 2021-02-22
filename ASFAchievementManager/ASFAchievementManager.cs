using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Composition;
using System.Globalization;
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
		[PublicAPI]
		public static readonly ConcurrentDictionary<Bot, AchievementHandler> AchievementHandlers = new();

		public async Task<string> OnBotCommand(Bot bot, ulong steamID, string message, string[] args) {
			return args[0].ToUpperInvariant() switch {
				"ALIST" when args.Length > 2 => await ResponseAchievementList(steamID, args[1], Utilities.GetArgsAsText(args, 2, ",")).ConfigureAwait(false),
				"ALIST" when args.Length > 1 => await ResponseAchievementList(steamID, bot, args[1]).ConfigureAwait(false),
				"ASET" when args.Length > 3 => await ResponseAchievementSet(steamID, args[1], args[2], Utilities.GetArgsAsText(args, 3, ","), true).ConfigureAwait(false),
				"ASET" when args.Length > 2 => await ResponseAchievementSet(steamID, bot, args[1], Utilities.GetArgsAsText(args, 2, ","), true).ConfigureAwait(false),
				"ARESET" when args.Length > 3 => await ResponseAchievementSet(steamID, args[1], args[2], Utilities.GetArgsAsText(args, 3, ","), false).ConfigureAwait(false),
				"ARESET" when args.Length > 2 => await ResponseAchievementSet(steamID, bot, args[1], Utilities.GetArgsAsText(args, 2, ","), false).ConfigureAwait(false),
				_ => null
			};
		}

		public string Name => "ASF Achievement Manager";
		public Version Version => typeof(ASFAchievementManager).Assembly.GetName().Version;

		public void OnLoaded() => ASF.ArchiLogger.LogGenericInfo("ASF Achievement Manager Plugin by Ryzhehvost, powered by ginger cats | Fork by Vital7");

		public void OnBotSteamCallbacksInit(Bot bot, CallbackManager callbackManager) {
		}

		public IReadOnlyCollection<ClientMsgHandler> OnBotSteamHandlersInit(Bot bot) {
			AchievementHandler currentBotAchievementHandler = new(bot);
			AchievementHandlers.TryAdd(bot, currentBotAchievementHandler);
			return new[] {currentBotAchievementHandler};
		}

		#region Responses

		private static async Task<string> ResponseAchievementList(ulong steamID, Bot bot, string appids) {
			if (!bot.HasAccess(steamID, BotConfig.EAccess.Master)) {
				return null;
			}

			string[] gameIDs = appids.Split(new[] {','}, StringSplitOptions.RemoveEmptyEntries);

			if (gameIDs.Length == 0) {
				return bot.Commands.FormatBotResponse(string.Format(CultureInfo.CurrentCulture, Strings.ErrorIsEmpty, nameof(gameIDs)));
			}

			if (AchievementHandlers.TryGetValue(bot, out AchievementHandler achievementHandler)) {
				HashSet<uint> gamesToGetAchievements = new();

				foreach (string game in gameIDs) {
					if (!uint.TryParse(game, out uint gameID) || (gameID == 0)) {
						return bot.Commands.FormatBotResponse(string.Format(CultureInfo.CurrentCulture, Strings.ErrorParsingObject, nameof(gameID)));
					}

					gamesToGetAchievements.Add(gameID);
				}


				IEnumerable<string> results = (await Utilities.InParallel(gamesToGetAchievements.Select(appID => achievementHandler.GetAchievements(appID))).ConfigureAwait(false)).Select(x => x.Response);
				List<string> responses = new(results.Where(result => !string.IsNullOrEmpty(result)));

				return responses.Count > 0 ? bot.Commands.FormatBotResponse(string.Join(Environment.NewLine, responses)) : null;
			}

			return bot.Commands.FormatBotResponse(string.Format(CultureInfo.CurrentCulture, Strings.ErrorIsEmpty, nameof(AchievementHandlers)));
		}

		private static async Task<string> ResponseAchievementList(ulong steamID, string botNames, string appids) {
			HashSet<Bot> bots = Bot.GetBots(botNames);
			if ((bots == null) || (bots.Count == 0)) {
				return Commands.FormatStaticResponse(string.Format(CultureInfo.CurrentCulture, Strings.BotNotFound, botNames));
			}

			IList<string> results = await Utilities.InParallel(bots.Select(bot => ResponseAchievementList(steamID, bot, appids))).ConfigureAwait(false);

			List<string> responses = new(results.Where(result => !string.IsNullOrEmpty(result)));

			return responses.Count > 0 ? string.Join(Environment.NewLine, responses) : null;
		}


		private static async Task<string> ResponseAchievementSet(ulong steamID, Bot bot, string appid, string achievementNumbers, bool set) {
			if (!bot.HasAccess(steamID, BotConfig.EAccess.Master)) {
				return null;
			}

			if (string.IsNullOrEmpty(achievementNumbers)) {
				return bot.Commands.FormatBotResponse(string.Format(CultureInfo.CurrentCulture, Strings.ErrorObjectIsNull, nameof(achievementNumbers)));
			}

			if (!uint.TryParse(appid, out uint appId)) {
				return bot.Commands.FormatBotResponse(string.Format(CultureInfo.CurrentCulture, Strings.ErrorIsInvalid, nameof(appId)));
			}

			if (!AchievementHandlers.TryGetValue(bot, out AchievementHandler achievementHandler)) {
				return bot.Commands.FormatBotResponse(string.Format(CultureInfo.CurrentCulture, Strings.ErrorIsEmpty, nameof(AchievementHandlers)));
			}

			HashSet<uint> achievements = new();

			string[] achievementStrings = achievementNumbers.Split(new[] {','}, StringSplitOptions.RemoveEmptyEntries);

			if (!achievementNumbers.Equals("*")) {
				foreach (string achievement in achievementStrings) {
					if (!uint.TryParse(achievement, out uint achievementNumber) || (achievementNumber == 0)) {
						return bot.Commands.FormatBotResponse(string.Format(CultureInfo.CurrentCulture, Strings.ErrorParsingObject, achievement));
					}

					achievements.Add(achievementNumber);
				}

				if (achievements.Count == 0) {
					return bot.Commands.FormatBotResponse(string.Format(CultureInfo.CurrentCulture, Strings.ErrorIsEmpty, "Achievements list"));
				}
			}

			return bot.Commands.FormatBotResponse((await achievementHandler.SetAchievements(appId, achievements, set).ConfigureAwait(false)).Response);
		}

		private static async Task<string> ResponseAchievementSet(ulong steamID, string botNames, string appid, string achievementNumbers, bool set) {
			HashSet<Bot> bots = Bot.GetBots(botNames);
			if ((bots == null) || (bots.Count == 0)) {
				return Commands.FormatStaticResponse(string.Format(CultureInfo.CurrentCulture, Strings.BotNotFound, botNames));
			}

			IList<string> results = await Utilities.InParallel(bots.Select(bot => ResponseAchievementSet(steamID, bot, appid, achievementNumbers, set))).ConfigureAwait(false);

			List<string> responses = new(results.Where(result => !string.IsNullOrEmpty(result)));

			return responses.Count > 0 ? string.Join(Environment.NewLine, responses) : null;
		}

		#endregion
	}
}
