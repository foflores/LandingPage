// noinspection JSUnusedGlobalSymbols

'use strict';

function handler(event) {
    let request = event.request;
    let host = request.headers["host"].value;

    if (!host.startsWith("www.")) {
        let newHost = 'www.' + host;
        let newUrl = 'https://' + newHost + request.uri;

        return {
            statusCode: 302,
            headers: {
                'location': { "value": newUrl }
            }
        }
    }

    if (request.uri !== "/" && (request.uri.endsWith("/") || request.uri.lastIndexOf(".") < request.uri.lastIndexOf("/"))) {
        if (request.uri.endsWith("/")) {
            request.uri = request.uri.concat("index.html");
        } else {
            request.uri = request.uri.concat("/index.html");
        }
    }

    return request;
}
