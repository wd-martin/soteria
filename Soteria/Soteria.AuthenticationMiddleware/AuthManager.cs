﻿using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using System.Security.Claims;
using Soteria.AuthenticationMiddleware.UserInformation;
using System.Reflection;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace Soteria.AuthenticationMiddleware
{
    public static class AuthManager
    {
        public const string MiddleWareInstanceName = "Soteria";
        private static SymmetricSecurityKey _key;

        public static void InitializeAuthenticationService<TPermissionHandler, TSessionHandler, GenericUser>(
            this IServiceCollection serviceCollection, 
            string loginPath, 
            string windowsLoginPath, 
            string accessDeniedPath, 
            string logoutPath, 
            bool forceSecureCookie,
            int defaultExpireMinutes,
            SymmetricSecurityKey key,
            string hostDomain,
            bool cookieHttpOnly = true
            ) 
            where GenericUser: class, new()
            where TPermissionHandler : class, IPermissionHandler
            where TSessionHandler : class, ISessionHandler
        {
            _key = key;
            serviceCollection.AddScoped<UserService<GenericUser>>();
            serviceCollection.AddScoped<IPermissionHandler, TPermissionHandler>();
            serviceCollection.AddScoped<ISessionHandler, TSessionHandler>();
            serviceCollection.AddTransient<IHttpContextAccessor, HttpContextAccessor>();
            serviceCollection.AddTransient<IAuthorizationHandler, MiddlewareAuthorizationHandler>();
            serviceCollection.AddAuthorization(options =>
            {
                options.AddPolicy(MiddleWareInstanceName, policyBuilder =>
                {
                    policyBuilder.Requirements.Add(new MiddlewareAuthorizationRequirment());
                });
            });

            var jwtTokenParameters = CreateTokenParameters(key, hostDomain, hostDomain, $"{MiddleWareInstanceName}-jwt");
            var soteriaJwtValidator = new SoteriaJwtValidator(SecurityAlgorithms.HmacSha256, jwtTokenParameters);
            serviceCollection.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = $"{MiddleWareInstanceName}";
                options.DefaultChallengeScheme = $"{MiddleWareInstanceName}";
                options.DefaultScheme = $"{MiddleWareInstanceName}";

            })
            .AddCookie(MiddleWareInstanceName, cookie =>
            {
                SetCookieAuthenticationOptions(cookie, loginPath, windowsLoginPath, accessDeniedPath, logoutPath, forceSecureCookie, defaultExpireMinutes, cookieHttpOnly);
            })
            .AddScheme<JwtBearerOptions, SoteriaJwTHandler>($"{MiddleWareInstanceName}-jwt", null, jwt =>
            {
                SetJWTBearerOptions(jwt, key, jwtTokenParameters, soteriaJwtValidator, forceSecureCookie);
            });    
        }

        public static void InitializeAppAuthentication(this IApplicationBuilder app)
        {
            app.UseAuthentication();
        }

        public static void InitializeAppAuthorization(this IApplicationBuilder app)
        {
            app.UseAuthorization();
        }

        private static void SetJWTBearerOptions(JwtBearerOptions jwt, 
            SymmetricSecurityKey key, 
            TokenValidationParameters jwtTokenParameters, 
            SoteriaJwtValidator soteriaJwtValidator, 
            bool requireHttps)
        {
            jwt.RequireHttpsMetadata = requireHttps;
            jwt.Events = new JwtBearerEvents
            {
                OnAuthenticationFailed = async ctx =>
                {
                    var identity = ctx.HttpContext.User?.GetSoteriaIdentity();
                    if (identity != null && identity.IsAuthenticated)
                    {
                        ctx.Response.StatusCode = 403;
                        await ctx.Response.WriteAsync(ctx.Exception.Message);
                        return;
                    }
                },
                OnChallenge = async ctx =>
                {
                    ctx.HandleResponse();
                    ctx.Response.StatusCode = 401;
                    var message = ctx.ErrorDescription != null ? ctx.ErrorDescription : "Unauthenticated";
                    await ctx.Response.WriteAsync(message);
                    return;
                },
                OnForbidden = async ctx =>
                {
                    var identity = ctx.HttpContext.User?.GetSoteriaIdentity();
                    if (identity != null && identity.IsAuthenticated)
                    {
                        ctx.Response.StatusCode = 403;
                        await ctx.Response.WriteAsync("Forbidden");
                        return;
                    }
                }
            };
            
            jwt.SecurityTokenValidators.Add(soteriaJwtValidator);
            jwt.TokenValidationParameters = jwtTokenParameters;
        }

        private static void SetCookieAuthenticationOptions(CookieAuthenticationOptions cookie, 
            string loginPath, 
            string windowsLoginPath, 
            string accessDeniedPath, 
            string logoutPath, 
            bool forceSecureCookie, 
            int defaultExpireMinutes, 
            bool cookieHttpOnly)
        {
            cookie.LoginPath = new PathString(loginPath);
            cookie.LogoutPath = new PathString(logoutPath);
            cookie.AccessDeniedPath = accessDeniedPath;
            cookie.Cookie.Name = MiddleWareInstanceName;
            cookie.Cookie.SecurePolicy = forceSecureCookie ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
            cookie.SlidingExpiration = true;
            cookie.ExpireTimeSpan = TimeSpan.FromMinutes(defaultExpireMinutes);
            cookie.Cookie.HttpOnly = cookieHttpOnly;
            cookie.Events = new CookieAuthenticationEvents
            {
                OnValidatePrincipal = ctx =>
                {
                    return Task.CompletedTask;
                },
                OnSigningIn = ctx =>
                {
                    var expireTime = (ctx.CookieOptions.Expires ?? DateTime.Now.AddMinutes(defaultExpireMinutes)) - DateTime.Now;
                    ctx.Options.ExpireTimeSpan = expireTime;
                    return Task.FromResult(0);
                },
                OnRedirectToLogin = async ctx =>
                {
                    if (ctx.Request.IsAjaxRequest())
                    {
                        ctx.HttpContext.Response.StatusCode = 401;
                        await ctx.Response.WriteAsync("Unauthenticated");
                        return;
                    }
                    var requestBase = GetRequestBasePath(ctx);
                    var queryString = "";
                    if (ctx.Request.Query.Count > 0)
                        queryString = "?" + string.Join("&", ctx.Request.Query.Select(t => $"{t.Key}={t.Value}"));
                    var redirectTo = System.Net.WebUtility.UrlEncode($"{requestBase}{ctx.Request.Path}{queryString}");

                    var shouldReturn = ctx.Request.Path.HasValue &&
                        (ctx.Request.Path.Value.ToLower().EndsWith(windowsLoginPath.ToLower())
                        || ctx.Request.Path.Value.ToLower().EndsWith($"{windowsLoginPath}test".ToLower()));
                    if (shouldReturn)
                        return;
                    ctx.Response.Redirect($"{requestBase}{ctx.Options.LoginPath}?ReturnUrl={redirectTo}");
                    
                    return;
                },
                OnRedirectToAccessDenied = async ctx =>
                {
                    if (ctx.Request.IsAjaxRequest())
                    {
                        ctx.HttpContext.Response.StatusCode = 403;
                        await ctx.Response.WriteAsync("Unauthorized");
                        return;
                    }
                    var requestBase = GetRequestBasePath(ctx);
                    ctx.Response.Redirect($"{requestBase}{ctx.Options.AccessDeniedPath}");
                    return;
                }
            };
        }

        private static TokenValidationParameters CreateTokenParameters(SymmetricSecurityKey key, 
            string issuer, 
            string audience, 
            string authType)
        {
            return new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = key,
                ValidateIssuer = true,
                ValidIssuer = issuer,
                ValidateAudience = true,
                ValidAudience = audience,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(3),
                AuthenticationType = authType
            };
        }
        private static string GetRequestBasePath(RedirectContext<CookieAuthenticationOptions> ctx)
        {
            var requestBase = "";
            if (!ctx.Request.Host.Value.EndsWith("/"))
                requestBase = "/";
            if (ctx.Request.PathBase.ToString().Trim() != "/")
                requestBase = ctx.Request.PathBase;
            if (!requestBase.StartsWith("/") && requestBase != "/")
                requestBase = $"/{requestBase}";
            
            var path = $"{ctx.Request.Scheme}://{ctx.Request.Host}{requestBase}";
            return path.TrimEnd('/');
        }

        public static async Task<ClaimsIdentity> UserSignOnAsync<T>(this HttpContext context, 
            string userName, 
            Guid sessionGuid,
            AuthenticationMethod authenticateddBy, 
            T genericUser,
            bool isPersistant) where T : class, new()
        {

            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.Name, userName),
                new Claim(ClaimTypes.NameIdentifier, userName.EnsureNullIsEmpty()),
                new Claim(nameof(SoteriaUser<T>.SessionGuid), sessionGuid.ToString()),
                new Claim(nameof(SoteriaUser<T>.UserName), userName.EnsureNullIsEmpty()),
                new Claim(nameof(SoteriaUser<T>.AuthenticatedBy), authenticateddBy.ToString()),
                new Claim(nameof(SoteriaUser<T>.IsCookiePersistant), isPersistant.ToString()),
                new Claim(nameof(SoteriaUser<T>.GenericTypeName), typeof(T).Name)
            };
            foreach (var item in typeof(T).GetProperties())
            {
                var val = item.GetValue(genericUser);
                claims.Add(new Claim(item.Name, Newtonsoft.Json.JsonConvert.SerializeObject(val)));
            }

            var claim = new ClaimsIdentity(claims, MiddleWareInstanceName);

            var expireIn = TimeSpan.FromHours(8);
            if (isPersistant)
                expireIn = TimeSpan.FromDays(30);

            var authProperties = new Microsoft.AspNetCore.Authentication.AuthenticationProperties { IsPersistent = isPersistant, ExpiresUtc = DateTime.UtcNow.Add(expireIn), AllowRefresh = true };
            await context.SignInAsync(MiddleWareInstanceName, new ClaimsPrincipal(claim), authProperties).ConfigureAwait(false);
            context.GetSessionManager().AddNewSession(sessionGuid);
            return claim;
            
        }
        public static async Task UserSignOutAsync(this HttpContext context, ISessionHandler sessionHandler)
        {
            var customUser = context.User.Identities.SingleOrDefault(t => t.AuthenticationType == MiddleWareInstanceName);
            if (customUser?.Name != null)
                PermissionManager.ClearPermissions(customUser.Name);
            var customSessionGuid = customUser?.FindFirst("SessionGuid")?.Value;
            if (!string.IsNullOrWhiteSpace(customSessionGuid))
            {
                var customUserGuid = new Guid(customSessionGuid);
                SessionManager.RemoveGuid(customUserGuid);
                await sessionHandler.DeleteSessionAsync(customUserGuid);
            }
            await AuthenticationHttpContextExtensions.SignOutAsync(context, MiddleWareInstanceName);
            
        }
        public static async Task<string> JWTUserSignOnAsync<T>(this HttpContext context,
            string userName,
            Guid sessionGuid,
            AuthenticationMethod authenticateddBy,
            T genericUser,
            int expireInMinutes,
            string hostDomain) where T: class, new()
        {
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.Name, userName),
                new Claim(ClaimTypes.NameIdentifier, userName.EnsureNullIsEmpty()),
                new Claim(nameof(SoteriaUser<T>.SessionGuid), sessionGuid.ToString()),
                new Claim(nameof(SoteriaUser<T>.UserName), userName.EnsureNullIsEmpty()),
                new Claim(nameof(SoteriaUser<T>.AuthenticatedBy), authenticateddBy.ToString()),
                new Claim(nameof(SoteriaUser<T>.IsCookiePersistant), false.ToString()),
                new Claim(nameof(SoteriaUser<T>.GenericTypeName), typeof(T).Name)
            };
            foreach (var item in typeof(T).GetProperties())
            {
                var val = item.GetValue(genericUser);
                claims.Add(new Claim(item.Name, Newtonsoft.Json.JsonConvert.SerializeObject(val)));
            }

            var _ = new ClaimsIdentity(claims, MiddleWareInstanceName);
            var soteriaJwtDataFormat = new SoteriaJwtDataFormat(SecurityAlgorithms.HmacSha256, CreateTokenParameters(_key, hostDomain, hostDomain, $"{MiddleWareInstanceName}-jwt"));
            return await Task.FromResult(soteriaJwtDataFormat.CreateJWT(claims, DateTime.Now, DateTime.Now.AddMinutes(expireInMinutes)));

        }
        public static List<string> GetAllAssignedPermissions(Assembly assembly)
        {
            var permissions = new HashSet<string>();
            var attributeClassUsage = from type in assembly.GetTypes()
                                      where Attribute.IsDefined(type, typeof(SoteriaPermissionCheck))
                                      select type;
            foreach (var cls in attributeClassUsage)
            {
                var authorization = cls.GetCustomAttributes(typeof(SoteriaPermissionCheck));
                foreach (var item in authorization.SelectMany(t => ((SoteriaPermissionCheck)t).PermissionList))
                {
                    permissions.Add(item.Trim());
                }
            }
            var methodUsage = (from type in assembly.GetTypes()
                               from method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                               where Attribute.IsDefined(method, typeof(SoteriaPermissionCheck))
                               select method);
            foreach (var method in methodUsage)
            {
                var authorization = method.GetCustomAttributes(typeof(SoteriaPermissionCheck));
                foreach (var item in authorization.SelectMany(t => ((SoteriaPermissionCheck)t).PermissionList))
                {
                    permissions.Add(item.Trim());
                }
            }

            return permissions.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        }

    }
}
