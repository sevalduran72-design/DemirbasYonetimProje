import 'dart:convert';

import 'package:flutter/foundation.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:http/http.dart' as http;

class ApiException implements Exception {
  const ApiException(this.message, {this.statusCode});

  final String message;
  final int? statusCode;

  @override
  String toString() => message;
}

class AuthService {
  AuthService({http.Client? client}) : _client = client ?? http.Client();

  static const _cookieKey = 'demirbas_auth_cookie';
  static const _configuredApiUrl = String.fromEnvironment('API_BASE_URL');
  static const _storage = FlutterSecureStorage();

  final http.Client _client;
  String get baseUrl {
    if (_configuredApiUrl.isNotEmpty) return _configuredApiUrl;
    return defaultTargetPlatform == TargetPlatform.android
        ? 'http://10.0.2.2:5181/api'
        : 'http://localhost:5181/api';
  }

  Future<Map<String, dynamic>?> restoreSession() async {
    if (await _storage.read(key: _cookieKey) == null) return null;
    try {
      final response = await request('GET', 'auth/ben');
      return Map<String, dynamic>.from(response as Map);
    } on ApiException catch (error) {
      if (error.statusCode == 401) return null;
      rethrow;
    }
  }

  Future<Map<String, dynamic>> login(
    String username,
    String password, {
    required bool admin,
  }) async {
    final response = await request(
      'POST',
      admin ? 'auth/yonetici-giris' : 'auth/giris',
      body: {'kullaniciAdi': username, 'parola': password},
      clearSessionOnUnauthorized: false,
    );

    if (await _storage.read(key: _cookieKey) == null) {
      throw const ApiException(
        'Sunucu giriş yanıtında oturum çerezi göndermedi.',
      );
    }
    return Map<String, dynamic>.from(response as Map);
  }

  Future<void> logout() async {
    try {
      await request('POST', 'auth/cikis', body: const {});
    } finally {
      await _storage.delete(key: _cookieKey);
    }
  }

  Future<dynamic> request(
    String method,
    String path, {
    Map<String, String?> query = const {},
    Object? body,
    bool clearSessionOnUnauthorized = true,
  }) async {
    final uri = Uri.parse('$baseUrl/$path').replace(
      queryParameters: {
        for (final entry in query.entries)
          if (entry.value != null && entry.value!.isNotEmpty)
            entry.key: entry.value!,
      },
    );
    final headers = <String, String>{
      'Accept': 'application/json',
      'X-Requested-With': 'XMLHttpRequest',
    };
    final cookie = await _storage.read(key: _cookieKey);
    if (cookie != null) headers['Cookie'] = cookie;
    if (body != null) {
      headers['Content-Type'] = 'application/json; charset=UTF-8';
    }

    final request = http.Request(method, uri)..headers.addAll(headers);
    if (body != null) request.body = jsonEncode(body);

    final streamed = await _client
        .send(request)
        .timeout(const Duration(seconds: 20));
    final response = await http.Response.fromStream(streamed);
    final setCookie = response.headers['set-cookie'];
    if (setCookie != null && setCookie.isNotEmpty) {
      await _storage.write(
        key: _cookieKey,
        value: setCookie.split(';').first.trim(),
      );
    }

    final dynamic decoded;
    if (response.body.trim().isEmpty) {
      decoded = null;
    } else {
      try {
        decoded = jsonDecode(response.body);
      } on FormatException {
        throw ApiException(
          'Sunucudan geçersiz yanıt alındı (${response.statusCode}).',
          statusCode: response.statusCode,
        );
      }
    }

    if (response.statusCode < 200 || response.statusCode >= 300) {
      if (response.statusCode == 401 && clearSessionOnUnauthorized) {
        await _storage.delete(key: _cookieKey);
      }
      final message = decoded is Map
          ? decoded['mesaj'] ??
                decoded['detail'] ??
                decoded['title'] ??
                'İstek başarısız oldu.'
          : 'İstek başarısız oldu (${response.statusCode}).';
      throw ApiException(message.toString(), statusCode: response.statusCode);
    }

    return decoded;
  }
}
