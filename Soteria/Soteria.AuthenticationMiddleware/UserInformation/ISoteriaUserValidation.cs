﻿using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;

namespace Soteria.AuthenticationMiddleware.UserInformation
{
    public interface ISoteriaUserValidation
    {
        string ClaimID { get; }
        bool ValidClaimInformation { get; }
        ClaimsIdentity Identity { get; }
        bool IsAuthenticated { get; }
        Task<bool> IsInRole(string role);
    }
}
