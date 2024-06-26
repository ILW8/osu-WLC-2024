// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Tournament.Components;
using osu.Game.Tournament.Models;
using osuTK;

namespace osu.Game.Tournament.Screens.TeamIntro
{
    // ReSharper disable once PartialTypeWithSinglePart
    public partial class TeamIntroScreen : TournamentMatchScreen
    {
        private Container mainContainer = null!;
        public TournamentTeam? TourneyTeamLeft { get; private set; }
        public TournamentTeam? TourneyTeamRight { get; private set; }

        [BackgroundDependencyLoader]
        private void load()
        {
            RelativeSizeAxes = Axes.Both;

            InternalChildren = new Drawable[]
            {
                new TourneyVideo("teamintro")
                {
                    RelativeSizeAxes = Axes.Both,
                    Loop = true,
                },
                mainContainer = new Container
                {
                    RelativeSizeAxes = Axes.Both,
                }
            };
        }

        protected override void CurrentMatchChanged(ValueChangedEvent<TournamentMatch?> match)
        {
            base.CurrentMatchChanged(match);

            mainContainer.Clear();

            if (match.NewValue == null)
                return;

            TourneyTeamLeft = match.NewValue.Team1.Value;
            TourneyTeamRight = match.NewValue.Team2.Value;

            const float y_flag_screen_offset = 256f;
            // const float y_flag_relative_offset = 50f;
            const float x_flag_relative_offset = 128 + 16;

            const float flag_size_scale = 1f;

            const float y_offset = 460;

            mainContainer.Children = new Drawable[]
            {
                new RoundDisplay(match.NewValue)
                {
                    Position = new Vector2(100, 100)
                },
                new UserTile //
                {
                    User = TourneyTeamLeft?.Players.ElementAtOrDefault(2)?.ToAPIUser() ?? new APIUser(),
                    Position = new Vector2(160 + 2 * x_flag_relative_offset, y_flag_screen_offset),
                    Scale = new Vector2(flag_size_scale),
                    Margin = new MarginPadding { Right = 20 }
                },
                new UserTile // left team, bottom right
                {
                    User = TourneyTeamLeft?.Players.ElementAtOrDefault(1)?.ToAPIUser() ?? new APIUser(),
                    Position = new Vector2(160 + x_flag_relative_offset, y_flag_screen_offset),
                    Scale = new Vector2(flag_size_scale),
                    Margin = new MarginPadding { Right = 20 }
                },
                new UserTile // left team, top left
                {
                    User = TourneyTeamLeft?.Players.ElementAtOrDefault(0)?.ToAPIUser() ?? new APIUser(),
                    Position = new Vector2(160, y_flag_screen_offset),
                    Scale = new Vector2(flag_size_scale),
                    Margin = new MarginPadding { Right = 20 }
                },
                new DrawableTeamWithPlayers(match.NewValue.Team1.Value, TeamColour.Red)
                {
                    Position = new Vector2(165, y_offset),
                },
                new UserTile //
                {
                    User = TourneyTeamRight?.Players.ElementAtOrDefault(0)?.ToAPIUser() ?? new APIUser(),
                    Position = new Vector2(727, y_flag_screen_offset),
                    Scale = new Vector2(flag_size_scale),
                    Margin = new MarginPadding { Right = 20 }
                },
                new UserTile // right team, bottom left
                {
                    User = TourneyTeamRight?.Players.ElementAtOrDefault(1)?.ToAPIUser() ?? new APIUser(),
                    Position = new Vector2(727 + x_flag_relative_offset, y_flag_screen_offset),
                    Scale = new Vector2(flag_size_scale),
                    Margin = new MarginPadding { Right = 20 }
                },
                new UserTile // right team, top right
                {
                    User = TourneyTeamRight?.Players.ElementAtOrDefault(2)?.ToAPIUser() ?? new APIUser(),
                    Position = new Vector2(727 + 2 * x_flag_relative_offset, y_flag_screen_offset),
                    Scale = new Vector2(flag_size_scale),
                    Margin = new MarginPadding { Right = 20 }
                },
                new DrawableTeamWithPlayers(match.NewValue.Team2.Value, TeamColour.Blue)
                {
                    Position = new Vector2(740, y_offset),
                },
            };
        }
    }
}
