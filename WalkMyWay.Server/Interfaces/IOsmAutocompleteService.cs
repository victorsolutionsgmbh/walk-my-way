namespace WalkMyWay.Server.Services;

public interface IOsmAutocompleteService
{
    Task<List<PlaceSuggestion>> GetSuggestionsAsync(string input, double? lat, double? lng);
}
