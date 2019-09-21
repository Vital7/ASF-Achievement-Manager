using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm;
using ArchiSteamFarm.Localization;
using SteamKit2;
using SteamKit2.Internal;

namespace ASFAchievementManager {
	public sealed class AchievementHandler : ClientMsgHandler {
		private readonly ConcurrentDictionary<EMsg, (SemaphoreSlim Semaphore, ClientMsgProtobuf Message)> MessagesToWait = new ConcurrentDictionary<EMsg, (SemaphoreSlim Semaphore, ClientMsgProtobuf Message)>();

		public override void HandleMsg(IPacketMsg packetMsg) {
			if (packetMsg == null) {
				ASF.ArchiLogger.LogNullError(nameof(packetMsg));
				return;
			}

			switch (packetMsg.MsgType) {
				case EMsg.ClientGetUserStatsResponse:
					HandleGetUserStatsResponse(packetMsg);
					break;
				case EMsg.ClientStoreUserStatsResponse:
					HandleStoreUserStatsResponse(packetMsg);
					break;
			}
		}

		private async Task<T> GetResponse<T>(IClientMsg request, EMsg expectedResponseType) where T : ClientMsgProtobuf {
			if ((request == null) || (expectedResponseType == EMsg.Invalid)) {
				ASF.ArchiLogger.LogNullError(nameof(request));
				return null;
			}
			
			SemaphoreSlim semaphore = new SemaphoreSlim(0, 1);
			MessagesToWait[expectedResponseType] = (semaphore, null);
			Client.Send(request);

			if (!await semaphore.WaitAsync(TimeSpan.FromSeconds(ASF.GlobalConfig.ConnectionTimeout)).ConfigureAwait(false)) {
				ASF.ArchiLogger.LogGenericWarning(string.Format(Strings.ErrorFailingRequest, expectedResponseType.ToString()));
				return null;
			}

			ClientMsgProtobuf response = MessagesToWait[expectedResponseType].Message;
			
			return (T) response;
		}
		
		private void HandleGetUserStatsResponse(IPacketMsg packetMsg) {
			if (packetMsg == null) {
				ASF.ArchiLogger.LogNullError(nameof(packetMsg));
				return;
			}

			ClientMsgProtobuf<CMsgClientGetUserStatsResponse> response = new ClientMsgProtobuf<CMsgClientGetUserStatsResponse>(packetMsg);
			
			if (MessagesToWait.ContainsKey(packetMsg.MsgType)) {
				SemaphoreSlim semaphore = MessagesToWait[packetMsg.MsgType].Semaphore;
				MessagesToWait[packetMsg.MsgType] = (semaphore, response);
				semaphore.Release();
			}
		}

		private void HandleStoreUserStatsResponse(IPacketMsg packetMsg) {
			if (packetMsg == null) {
				ASF.ArchiLogger.LogNullError(nameof(packetMsg));
				return;
			}

			ClientMsgProtobuf<CMsgClientStoreUserStatsResponse> response = new ClientMsgProtobuf<CMsgClientStoreUserStatsResponse>(packetMsg);
			if (!response.Body.game_idSpecified) {
				ASF.ArchiLogger.LogNullError(nameof(response.Body.game_id));
			}
			
			if (MessagesToWait.ContainsKey(packetMsg.MsgType)) {
				SemaphoreSlim semaphore = MessagesToWait[packetMsg.MsgType].Semaphore;
				MessagesToWait[packetMsg.MsgType] = (semaphore, response);
				semaphore.Release();
			}
		}

		#region Utilities

		private static List<StatData> ParseResponse(CMsgClientGetUserStatsResponse response) {
			List<StatData> result = new List<StatData>();
			KeyValue keyValues = new KeyValue();
			if (response.schemaSpecified && (response.schema != null)) {
				using (MemoryStream ms = new MemoryStream(response.schema)) {
					if (!keyValues.TryReadAsBinary(ms)) {
						ASF.ArchiLogger.LogGenericError(string.Format(Strings.ErrorIsInvalid, nameof(response.schema)));
						return null;
					}
				}

				//first we enumerate all real achievements
				foreach (KeyValue stat in keyValues["stats"].Children) {
					if (stat["type"].Value == "4") {
						foreach (KeyValue achievement in stat["bits"].Children) {
							int bitNum = int.Parse(achievement.Name);
							uint statNum = uint.Parse(stat.Name);
							bool isSet = false;
							if (response.stats?.Find(statElement => statElement.stat_id == int.Parse(stat.Name)) != null) {
								isSet = (response.stats.Find(statElement => statElement.stat_id == int.Parse(stat.Name)).stat_value & ((uint) 1 << int.Parse(achievement.Name))) != 0;
							}

							bool restricted = achievement["permission"] != null;

							string dependencyName = achievement["progress"] == null ? "" : achievement["progress"]["value"]["operand1"].Value;

							uint dependencyValue = uint.Parse(achievement["progress"] == null ? "0" : achievement["progress"]["max_val"].Value);
							string lang = CultureInfo.CurrentUICulture.EnglishName.ToLower();
							if (lang.IndexOf('(') > 0) {
								lang = lang.Substring(0, lang.IndexOf('(') - 1);
							}

							if (achievement["display"]["name"].Children.Find(child => child.Name == lang) == null) {
								lang = "english"; //fallback to english
							}

							string name = achievement["display"]["name"].Children.Find(child => child.Name == lang).Value;

							result.Add(new StatData {
								StatNum = statNum,
								BitNum = bitNum,
								IsSet = isSet,
								Restricted = restricted,
								DependencyValue = dependencyValue,
								DependencyName = dependencyName,
								Dependency = 0,
								Name = name
							});
						}
					}
				}

				//Now we update all dependencies
				foreach (KeyValue stat in keyValues["stats"].Children) {
					if (stat["type"].Value == "1") {
						uint statNum = uint.Parse(stat.Name);
						bool restricted = stat["permission"] != null;
						string name = stat["name"].Value;
						StatData parentStat = result.Find(item => item.DependencyName == name);
						if (parentStat != null) {
							parentStat.Dependency = statNum;
							if (restricted && !parentStat.Restricted) {
								parentStat.Restricted = true;
							}
						}
					}
				}
			}

			return result;
		}

		private static void SetStat(List<CMsgClientStoreUserStats2.Stats> statsToSet, IReadOnlyList<StatData> stats, CMsgClientGetUserStatsResponse storedResponse, int achievementNum, bool set = true) {
			if ((achievementNum < 0) || (achievementNum > stats.Count)) {
				return; //it should never happen
			}

			CMsgClientStoreUserStats2.Stats currentstat = statsToSet.Find(stat => stat.stat_id == stats[achievementNum].StatNum);
			if (currentstat == null) {
				currentstat = new CMsgClientStoreUserStats2.Stats {
					stat_id = stats[achievementNum].StatNum,
					stat_value = storedResponse.stats.Find(stat => stat.stat_id == stats[achievementNum].StatNum) != null ? storedResponse.stats.Find(stat => stat.stat_id == stats[achievementNum].StatNum).stat_value : 0
				};
				statsToSet.Add(currentstat);
			}

			if (set) {
				currentstat.stat_value |= (uint) 1 << stats[achievementNum].BitNum;
			} else {
				currentstat.stat_value &= ~((uint) 1 << stats[achievementNum].BitNum);
			}

			if (stats[achievementNum].DependencyName != "") {
				CMsgClientStoreUserStats2.Stats dependencyStat = statsToSet.Find(stat => stat.stat_id == stats[achievementNum].Dependency);
				if (dependencyStat == null) {
					dependencyStat = new CMsgClientStoreUserStats2.Stats {
						stat_id = stats[achievementNum].Dependency,
						stat_value = set ? stats[achievementNum].DependencyValue : 0
					};

					statsToSet.Add(dependencyStat);
				}
			}
		}
		
		#endregion

		#region Endpoints

		internal async Task<string> GetAchievements(Bot bot, ulong gameID) {
			if ((bot == null) || (gameID == 0)) {
				ASF.ArchiLogger.LogNullError(nameof(bot) + " || " + nameof(gameID));
				return null;
			}
			
			if (!Client.IsConnected) {
				return Strings.BotNotConnected;
			}

			ClientMsgProtobuf<CMsgClientGetUserStats> request = new ClientMsgProtobuf<CMsgClientGetUserStats>(EMsg.ClientGetUserStats) {
				Body = {
					game_id = gameID,
					steam_id_for_user = bot.SteamID
				}
			};


			ClientMsgProtobuf<CMsgClientGetUserStatsResponse> userStatsResponse = await GetResponse<ClientMsgProtobuf<CMsgClientGetUserStatsResponse>>(request, EMsg.ClientGetUserStatsResponse).ConfigureAwait(false);
			if (userStatsResponse == null) {
				return "Can't retrieve achievements for " + gameID;
			}

			List<string> responses = new List<string>();
			List<StatData> stats = ParseResponse(userStatsResponse.Body);

			const char checkMarkEmoji = '\u2705';
			const char crossMarkEmoji = '\u274C';
			const string warningEmoji = "\u26A0\uFE0F";

			if ((stats == null) || (stats.Count == 0)) {
				bot.ArchiLogger.LogNullError(nameof(stats));
			} else {
				responses = stats.Select(stat => $"{stats.IndexOf(stat) + 1,-5}{(stat.IsSet ? checkMarkEmoji : crossMarkEmoji)} {(stat.Restricted ? warningEmoji + " " : "")}{stat.Name}").ToList();
			}

			return responses.Count > 0 ? "\u200B\nAchievemens for " + gameID + ":\n" + string.Join(Environment.NewLine, responses) : "Can't retrieve achievements for " + gameID;
		}

		internal async Task<string> SetAchievements(Bot bot, uint appId, HashSet<uint> achievements, bool set = true) {
			if ((bot == null) || (appId == 0) || (achievements == null) || (achievements.Count == 0)) {
				ASF.ArchiLogger.LogNullError(nameof(bot) + " || " + nameof(appId) + " || " + nameof(achievements));
				return null;
			}
			
			if (!Client.IsConnected) {
				return Strings.BotNotConnected;
			}

			List<string> responses = new List<string>();
			string getAchievementsResult = await GetAchievements(bot, appId).ConfigureAwait(false);

			ClientMsgProtobuf<CMsgClientGetUserStatsResponse> message = (ClientMsgProtobuf<CMsgClientGetUserStatsResponse>) MessagesToWait[EMsg.ClientGetUserStatsResponse].Message;
			if ((message == null) || (message.Body.eresult != 1)) {
				return getAchievementsResult;
			}

			List<StatData> stats = ParseResponse(message.Body);
			if (stats == null) {
				responses.Add(Strings.WarningFailed);
				return "\u200B\n" + string.Join(Environment.NewLine, responses);
			}

			List<CMsgClientStoreUserStats2.Stats> statsToSet = new List<CMsgClientStoreUserStats2.Stats>();

			if (achievements.Count == 0) {
				//if no parameters provided - set/reset all. Don't kill me Archi.
				for (int counter = 0; counter < stats.Count; counter++) {
					if (!stats[counter].Restricted) {
						SetStat(statsToSet, stats, message.Body, counter, set);
					}
				}
			} else {
				foreach (uint achievement in achievements) {
					if (stats.Count < achievement) {
						responses.Add("Achievement #" + achievement + " is out of range");
						continue;
					}

					if (stats[(int) achievement - 1].IsSet == set) {
						responses.Add("Achievement #" + achievement + " is already " + (set ? "unlocked" : "locked"));
						continue;
					}

					if (stats[(int) achievement - 1].Restricted) {
						responses.Add("Achievement #" + achievement + " is protected and can't be switched");
						continue;
					}

					SetStat(statsToSet, stats, message.Body, (int) achievement - 1, set);
				}
			}

			if (statsToSet.Count == 0) {
				responses.Add(Strings.WarningFailed);
				return "\u200B\n" + string.Join(Environment.NewLine, responses);
			}

			if (responses.Count > 0) {
				responses.Add("Trying to switch remaining achievements..."); //if some errors occured
			}

			ClientMsgProtobuf<CMsgClientStoreUserStats2> request = new ClientMsgProtobuf<CMsgClientStoreUserStats2>(EMsg.ClientStoreUserStats2) {
				Body = {
					game_id = appId,
					settor_steam_id = bot.SteamID,
					settee_steam_id = bot.SteamID,
					explicit_reset = false,
					crc_stats = message.Body.crc_stats
				}
			};

			request.Body.stats.AddRange(statsToSet);
			
			ClientMsgProtobuf<CMsgClientStoreUserStatsResponse> storeResponse = await GetResponse<ClientMsgProtobuf<CMsgClientStoreUserStatsResponse>>(request, EMsg.ClientStoreUserStatsResponse).ConfigureAwait(false);

			responses.Add((storeResponse == null) || (storeResponse.Body.eresult == 1) ? Strings.WarningFailed : Strings.Success);
			return "\u200B\n" + string.Join(Environment.NewLine, responses);
		}
		
		#endregion
	}
}
