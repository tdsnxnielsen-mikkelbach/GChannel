using GChannel.ApiService.Configuration;
using GChannel.Shared.Contracts;
using Google.Apis.Cloudchannel.v1;
using Google.Apis.Cloudchannel.v1.Data;
using Google.Apis.Http;
using Google.Apis.Services;
using Google.Apis.Util;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Net;

namespace GChannel.ApiService.Services;

/// <summary>Thrown when the inbound request carries no Google access token.</summary>
public sealed class MissingGoogleTokenException()
    : InvalidOperationException("No Google access token was supplied on the request.");
