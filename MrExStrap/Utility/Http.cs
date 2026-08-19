namespace BeastStrap.Utility
{
    internal static class Http
    {
        /// <summary>
        /// Gets and deserializes a JSON API response to the specified object.
        /// Returns null when the body deserializes to null (e.g. a literal "null"
        /// response) — callers must handle it rather than trusting a non-null result.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="url"></param>
        /// <param name="token"></param>
        /// <exception cref="HttpRequestException"></exception>
        /// <exception cref="JsonException"></exception>
        /// <exception cref="OperationCanceledException"></exception>
        public static async Task<T?> GetJson<T>(string url, CancellationToken token = default)
        {
            using var response = await App.HttpClient.GetAsync(url, token);

            response.EnsureSuccessStatusCode();

            string json = await response.Content.ReadAsStringAsync(token);

            return JsonSerializer.Deserialize<T>(json);
        }
    }
}
