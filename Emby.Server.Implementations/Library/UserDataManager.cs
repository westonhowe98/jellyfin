#nullable disable

#pragma warning disable CS1591

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Jellyfin.Data.Entities;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using AudioBook = MediaBrowser.Controller.Entities.AudioBook;
using Book = MediaBrowser.Controller.Entities.Book;

namespace Emby.Server.Implementations.Library
{
    /// <summary>
    /// Class UserDataManager.
    /// </summary>
    public class UserDataManager : IUserDataManager
    {
        private readonly ConcurrentDictionary<string, UserItemData> _userData =
            new ConcurrentDictionary<string, UserItemData>(StringComparer.OrdinalIgnoreCase);

        private readonly IServerConfigurationManager _config;
        private readonly IUserManager _userManager;
        private readonly IUserDataRepository _repository;

        public UserDataManager(
            IServerConfigurationManager config,
            IUserManager userManager,
            IUserDataRepository repository)
        {
            _config = config;
            _userManager = userManager;
            _repository = repository;
        }

        public event EventHandler<UserDataSaveEventArgs> UserDataSaved;

        public void SaveUserData(Guid userId, BaseItem item, UserItemData userData, UserDataSaveReason reason, CancellationToken cancellationToken)
        {
            var user = _userManager.GetUserById(userId);

            SaveUserData(user, item, userData, reason, cancellationToken);
        }

        public void SaveUserData(User user, BaseItem item, UserItemData userData, UserDataSaveReason reason, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(userData);

            ArgumentNullException.ThrowIfNull(item);

            cancellationToken.ThrowIfCancellationRequested();

            var keys = item.GetUserDataKeys();

            var userId = user.InternalId;

            foreach (var key in keys)
            {
                _repository.SaveUserData(userId, key, userData, cancellationToken);
            }

            var cacheKey = GetCacheKey(userId, item.Id);
            _userData.AddOrUpdate(cacheKey, userData, (_, _) => userData);

            UserDataSaved?.Invoke(this, new UserDataSaveEventArgs
            {
                Keys = keys,
                UserData = userData,
                SaveReason = reason,
                UserId = user.Id,
                Item = item
            });
        }

        /// <summary>
        /// Save the provided user data for the given user.  Batch operation. Does not fire any events or update the cache.
        /// </summary>
        /// <param name="userId">The user id.</param>
        /// <param name="userData">The user item data.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        public void SaveAllUserData(Guid userId, UserItemData[] userData, CancellationToken cancellationToken)
        {
            var user = _userManager.GetUserById(userId);

            _repository.SaveAllUserData(user.InternalId, userData, cancellationToken);
        }

        /// <summary>
        /// Retrieve all user data for the given user.
        /// </summary>
        /// <param name="userId">The user id.</param>
        /// <returns>A <see cref="List{UserItemData}"/> containing all of the user's item data.</returns>
        public List<UserItemData> GetAllUserData(Guid userId)
        {
            var user = _userManager.GetUserById(userId);

            return _repository.GetAllUserData(user.InternalId);
        }

        public UserItemData GetUserData(Guid userId, Guid itemId, List<string> keys)
        {
            var user = _userManager.GetUserById(userId);

            return GetUserData(user, itemId, keys);
        }

        public UserItemData GetUserData(User user, Guid itemId, List<string> keys)
        {
            var userId = user.InternalId;

            var cacheKey = GetCacheKey(userId, itemId);

            return _userData.GetOrAdd(cacheKey, _ => GetUserDataInternal(userId, keys));
        }

        private UserItemData GetUserDataInternal(long internalUserId, List<string> keys)
        {
            var userData = _repository.GetUserData(internalUserId, keys);

            if (userData is not null)
            {
                return userData;
            }

            if (keys.Count > 0)
            {
                return new UserItemData
                {
                    Key = keys[0]
                };
            }

            return null;
        }

        /// <summary>
        /// Gets the internal key.
        /// </summary>
        /// <returns>System.String.</returns>
        private static string GetCacheKey(long internalUserId, Guid itemId)
        {
            return internalUserId.ToString(CultureInfo.InvariantCulture) + "-" + itemId.ToString("N", CultureInfo.InvariantCulture);
        }

        public UserItemData GetUserData(User user, BaseItem item)
        {
            return GetUserData(user, item.Id, item.GetUserDataKeys());
        }

        public UserItemData GetUserData(Guid userId, BaseItem item)
        {
            return GetUserData(userId, item.Id, item.GetUserDataKeys());
        }

        public UserItemDataDto GetUserDataDto(BaseItem item, User user)
        {
            var userData = GetUserData(user, item);
            var dto = GetUserItemDataDto(userData);

            item.FillUserDataDtoValues(dto, userData, null, user, new DtoOptions());
            return dto;
        }

        /// <inheritdoc />
        public UserItemDataDto GetUserDataDto(BaseItem item, BaseItemDto itemDto, User user, DtoOptions options)
        {
            var userData = GetUserData(user, item);
            var dto = GetUserItemDataDto(userData);

            item.FillUserDataDtoValues(dto, userData, itemDto, user, options);
            return dto;
        }

        /// <summary>
        /// Converts a UserItemData to a DTOUserItemData.
        /// </summary>
        /// <param name="data">The data.</param>
        /// <returns>DtoUserItemData.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="data"/> is <c>null</c>.</exception>
        private UserItemDataDto GetUserItemDataDto(UserItemData data)
        {
            ArgumentNullException.ThrowIfNull(data);

            return new UserItemDataDto
            {
                IsFavorite = data.IsFavorite,
                Likes = data.Likes,
                PlaybackPositionTicks = data.PlaybackPositionTicks,
                PlayCount = data.PlayCount,
                Rating = data.Rating,
                Played = data.Played,
                LastPlayedDate = data.LastPlayedDate,
                Key = data.Key,
                IsExcludedFromContinueWatching = data.IsExcludedFromContinueWatching
            };
        }

        /// <inheritdoc />
        public bool UpdatePlayState(BaseItem item, UserItemData data, long? reportedPositionTicks)
        {
            var playedToCompletion = false;

            var runtimeTicks = item.GetRunTimeTicksForPlayState();

            var positionTicks = reportedPositionTicks ?? runtimeTicks;
            var hasRuntime = runtimeTicks > 0;

            // If a position has been reported, and if we know the duration
            if (positionTicks > 0 && hasRuntime && item is not AudioBook && item is not Book)
            {
                var pctIn = decimal.Divide(positionTicks, runtimeTicks) * 100;

                if (pctIn < _config.Configuration.MinResumePct)
                {
                    // ignore progress during the beginning
                    positionTicks = 0;
                }
                else if (pctIn > _config.Configuration.MaxResumePct || positionTicks >= runtimeTicks)
                {
                    // mark as completed close to the end
                    positionTicks = 0;
                    data.Played = playedToCompletion = true;
                }
                else
                {
                    // Enforce MinResumeDuration
                    var durationSeconds = TimeSpan.FromTicks(runtimeTicks).TotalSeconds;
                    if (durationSeconds < _config.Configuration.MinResumeDurationSeconds)
                    {
                        positionTicks = 0;
                        data.Played = playedToCompletion = true;
                    }
                }
            }
            else if (positionTicks > 0 && hasRuntime && item is AudioBook)
            {
                var playbackPositionInMinutes = TimeSpan.FromTicks(positionTicks).TotalMinutes;
                var remainingTimeInMinutes = TimeSpan.FromTicks(runtimeTicks - positionTicks).TotalMinutes;

                if (playbackPositionInMinutes < _config.Configuration.MinAudiobookResume)
                {
                    // ignore progress during the beginning
                    positionTicks = 0;
                }
                else if (remainingTimeInMinutes < _config.Configuration.MaxAudiobookResume || positionTicks >= runtimeTicks)
                {
                    // mark as completed close to the end
                    positionTicks = 0;
                    data.Played = playedToCompletion = true;
                }
            }
            else if (!hasRuntime)
            {
                // If we don't know the runtime we'll just have to assume it was fully played
                data.Played = playedToCompletion = true;
                positionTicks = 0;
            }

            if (!item.SupportsPlayedStatus)
            {
                positionTicks = 0;
                data.Played = false;
            }

            if (!item.SupportsPositionTicksResume)
            {
                positionTicks = 0;
            }

            if (data.LastPlayedDate.HasValue && data.PlaybackPositionTicks > 0)
            {
                var timeSinceLastPlayed = DateTime.UtcNow - data.LastPlayedDate.Value;

                if (timeSinceLastPlayed.TotalSeconds > _config.Configuration.ContinueWatchingDeleteTime)
                {
                    data.IsExcludedFromContinueWatching = true;
                }
            }

            data.PlaybackPositionTicks = positionTicks;

            return playedToCompletion;
        }
    }
}
