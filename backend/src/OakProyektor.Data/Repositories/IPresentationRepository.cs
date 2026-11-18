using OakProyektor.Core.Library;

namespace OakProyektor.Data.Repositories;

public interface IPresentationRepository
{
    Task<IReadOnlyList<Presentation>> GetAllAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<PresentationSlide>> GetSlidesAsync(long presentationId, CancellationToken cancellationToken);
}
