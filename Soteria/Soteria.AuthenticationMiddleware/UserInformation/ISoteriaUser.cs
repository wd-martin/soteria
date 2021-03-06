﻿using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using System.Threading.Tasks;

namespace Soteria.AuthenticationMiddleware.UserInformation
{
    public interface ISoteriaUser<T> where T : class, new()
    {
        T UserProperties { get; }
        string UserName { get; }
        Guid SessionGuid { get; }
        void ChangeFieldValue<TValue>(Expression<Func<T, TValue>> field, TValue value);
    }
}
