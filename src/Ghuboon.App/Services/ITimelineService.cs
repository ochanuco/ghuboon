using System.Collections.Generic;
using Ghuboon.App.ViewModels;

namespace Ghuboon.App.Services;

public interface ITimelineService
{
    IReadOnlyList<TimelineItemViewModel> GetPlaceholderItems();
}
