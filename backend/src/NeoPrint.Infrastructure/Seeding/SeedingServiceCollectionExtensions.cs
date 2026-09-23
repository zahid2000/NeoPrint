using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

using NeoPrint.Domain.Admin;

namespace NeoPrint.Infrastructure.Seeding;

/// <summary>Registers the seeders.</summary>
public static class SeedingServiceCollectionExtensions
{
    public static IServiceCollection AddNeoPrintSeeding(this IServiceCollection services, bool isDevelopment)
    {
        // The ASP.NET Core Identity password hasher, as plan section 2.8 requires.
        services.AddSingleton<IPasswordHasher<AdminUser>, PasswordHasher<AdminUser>>();
        services.AddScoped<DatabaseSeeder>();

        if (isDevelopment)
        {
            services.AddScoped<DevelopmentSeeder>();
        }

        return services;
    }
}
