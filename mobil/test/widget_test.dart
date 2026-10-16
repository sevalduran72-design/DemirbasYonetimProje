import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mobil/screens/giris_ekrani.dart';

void main() {
  testWidgets('login form validates required fields', (tester) async {
    await tester.pumpWidget(
      MaterialApp(
        home: GirisEkrani(
          onLogin: (_, _, _) async => const {
            'kullaniciAdi': 'test',
            'rol': 'Admin',
          },
        ),
      ),
    );

    await tester.tap(find.text('Giriş yap'));
    await tester.pumpAndSettle();

    expect(find.text('Kullanıcı adınızı girin.'), findsOneWidget);
    expect(find.text('Parolanızı girin.'), findsOneWidget);
  });

  testWidgets('login submits the selected account type', (tester) async {
    String? submittedUsername;
    bool? submittedAdmin;
    await tester.pumpWidget(
      MaterialApp(
        home: GirisEkrani(
          onLogin: (username, _, admin) async {
            submittedUsername = username;
            submittedAdmin = admin;
            return const {'kullaniciAdi': 'test', 'rol': 'Admin'};
          },
        ),
      ),
    );

    await tester.enterText(find.byType(TextFormField).at(0), '  testadmin  ');
    await tester.enterText(
      find.byType(TextFormField).at(1),
      'a-strong-password',
    );
    await tester.tap(find.text('Giriş yap'));
    await tester.pumpAndSettle();

    expect(submittedUsername, 'testadmin');
    expect(submittedAdmin, isTrue);
  });
}
