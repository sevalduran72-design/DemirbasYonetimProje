import 'package:flutter/material.dart';

import 'screens/ana_ekran.dart';
import 'screens/giris_ekrani.dart';
import 'services/auth_service.dart';

void main() {
  runApp(const DemirbasMobilApp());
}

class DemirbasMobilApp extends StatefulWidget {
  const DemirbasMobilApp({super.key});

  @override
  State<DemirbasMobilApp> createState() => _DemirbasMobilAppState();
}

class _DemirbasMobilAppState extends State<DemirbasMobilApp> {
  final _api = AuthService();
  late Future<Map<String, dynamic>?> _session;

  @override
  void initState() {
    super.initState();
    _session = _api.restoreSession();
  }

  Future<Map<String, dynamic>> _login(
    String username,
    String password,
    bool admin,
  ) async {
    final user = await _api.login(username, password, admin: admin);
    if (mounted) {
      setState(() {
        _session = Future.value(user);
      });
    }
    return user;
  }

  Future<void> _logout() async {
    await _api.logout();
    if (mounted) {
      setState(() {
        _session = Future.value(null);
      });
    }
  }

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      debugShowCheckedModeBanner: false,
      title: 'Demirbaş Yönetim Sistemi',
      theme: ThemeData(
        useMaterial3: true,
        colorScheme: ColorScheme.fromSeed(seedColor: const Color(0xFF1746A2)),
        scaffoldBackgroundColor: const Color(0xFFF4F7FC),
        inputDecorationTheme: InputDecorationTheme(
          filled: true,
          fillColor: Colors.white,
          border: OutlineInputBorder(borderRadius: BorderRadius.circular(14)),
          enabledBorder: OutlineInputBorder(
            borderRadius: BorderRadius.circular(14),
            borderSide: const BorderSide(color: Color(0xFFDCE4F0)),
          ),
        ),
      ),
      home: FutureBuilder<Map<String, dynamic>?>(
        future: _session,
        builder: (context, snapshot) {
          if (snapshot.hasError) {
            return _StartupError(
              message: snapshot.error.toString(),
              onRetry: () {
                setState(() {
                  _session = _api.restoreSession();
                });
              },
            );
          }
          if (snapshot.connectionState != ConnectionState.done) {
            return const Scaffold(
              body: Center(child: CircularProgressIndicator()),
            );
          }

          final user = snapshot.data;
          if (user == null) return GirisEkrani(onLogin: _login);
          return AnaEkran(api: _api, user: user, onLogout: _logout);
        },
      ),
    );
  }
}

class _StartupError extends StatelessWidget {
  const _StartupError({required this.message, required this.onRetry});

  final String message;
  final VoidCallback onRetry;

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      body: Center(
        child: Padding(
          padding: const EdgeInsets.all(24),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              const Icon(Icons.cloud_off_outlined, size: 48),
              const SizedBox(height: 16),
              Text(message, textAlign: TextAlign.center),
              const SizedBox(height: 16),
              FilledButton.icon(
                onPressed: onRetry,
                icon: const Icon(Icons.refresh),
                label: const Text('Yeniden dene'),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
